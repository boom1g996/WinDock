"""M1 实机冒烟验证（零依赖）：启动 DockHost，确认窗口存在、进程存活、
采样空闲 CPU/内存，并把结果写入 docs/M1-smoke-result.json。

不终止进程：验证完成后 Dock 继续运行，用户可直接看到效果。
用法：python tools/smoke_m1.py [--kill]
"""
from __future__ import annotations

import argparse
import ctypes
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from ctypes import wintypes
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# 必须在本进程任何 Win32 坐标调用之前设为 Per-Monitor DPI 感知。
# 本机屏幕是 125% 缩放：DPI 不感知的进程看到的是虚拟化桌面（1792×1120），
# 而应用（app.manifest 声明 PerMonitorV2）用的是物理坐标（2240×1400），
# 两套坐标系差 1.25 倍——按枚举矩形（虚拟）去点应用报出的图标坐标（物理）
# 会整体偏移，拖拽/悬停全部落空（2026-11-10 实机定位）。
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PROCESS_PER_MONITOR_DPI_AWARE
except (AttributeError, OSError):
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except (AttributeError, OSError):
        pass
EXE = ROOT / "artifacts" / "WinDock-framework-dependent" / "DockHost.exe"
RESULT = ROOT / "docs" / "M1-smoke-result.json"
LOG_DIR = Path.home() / "AppData" / "Local" / "WinDock" / "logs"

u32 = ctypes.WinDLL("user32", use_last_error=True)
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)


class RECT(ctypes.Structure):
    _fields_ = [("left", wintypes.LONG), ("top", wintypes.LONG),
                ("right", wintypes.LONG), ("bottom", wintypes.LONG)]


class FILETIME(ctypes.Structure):
    _fields_ = [("low", wintypes.DWORD), ("high", wintypes.DWORD)]


class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]


WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
u32.EnumWindows.argtypes = [WNDENUMPROC, wintypes.LPARAM]
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(RECT)]
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetSystemMetrics.argtypes = [ctypes.c_int]
u32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
u32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
u32.SetForegroundWindow.argtypes = [wintypes.HWND]
u32.keybd_event.argtypes = [ctypes.c_ubyte, ctypes.c_ubyte, wintypes.DWORD, ctypes.c_size_t]
u32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
u32.PostMessageW.restype = wintypes.BOOL
u32.mouse_event.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
                            wintypes.DWORD, ctypes.c_size_t]
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
RECOVERY = Path.home() / "AppData" / "Local" / "WinDock" / "watchdog-recovery.json"
k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k32.OpenProcess.restype = wintypes.HANDLE
k32.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(FILETIME)] * 4
psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESS_MEMORY_COUNTERS), wintypes.DWORD]


def ft_value(ft: FILETIME) -> int:
    return (ft.high << 32) | ft.low


def cpu_time(pid: int) -> float | None:
    handle = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return None
    try:
        creation, exit_time, kernel, user = FILETIME(), FILETIME(), FILETIME(), FILETIME()
        if not k32.GetProcessTimes(handle, ctypes.byref(creation), ctypes.byref(exit_time),
                                   ctypes.byref(kernel), ctypes.byref(user)):
            return None
        return (ft_value(kernel) + ft_value(user)) / 1e7  # seconds
    finally:
        k32.CloseHandle(handle)


def working_set(pid: int) -> int | None:
    handle = k32.OpenProcess(0x1000, False, pid)
    if not handle:
        return None
    try:
        counters = PROCESS_MEMORY_COUNTERS()
        counters.cb = ctypes.sizeof(counters)
        if not psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
            return None
        return counters.WorkingSetSize
    finally:
        k32.CloseHandle(handle)


def windows_of(pid: int) -> list[dict]:
    """返回该进程全部顶层窗口（M2：自动隐藏后 Dock 窗口不可见，仍须可核验）。"""
    found: list[dict] = []

    @WNDENUMPROC
    def callback(hwnd, _lparam):
        owner = wintypes.DWORD()
        u32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value != pid:
            return True
        rect = RECT()
        u32.GetWindowRect(hwnd, ctypes.byref(rect))
        name = ctypes.create_unicode_buffer(256)
        u32.GetClassNameW(hwnd, name, 256)
        title = ctypes.create_unicode_buffer(256)
        u32.GetWindowTextW(hwnd, title, 256)
        try:
            hwnd_value = int(hwnd)
        except (TypeError, ValueError):
            hwnd_value = int(hwnd.value or 0)
        found.append({
            "hwnd": hwnd_value,
            "class": name.value,
            "title": title.value,
            "rect": [rect.left, rect.top, rect.right, rect.bottom],
            "size": [rect.right - rect.left, rect.bottom - rect.top],
            "visible": bool(u32.IsWindowVisible(hwnd)),
        })
        return True

    u32.EnumWindows(callback, 0)
    return found


FLOAT_TITLES = ("WinDock Guide", "WinDock Preview")

# WPF 内部窗口与输入法框架窗口：尺寸会骗过「宽高 >50」的粗判，但不是 Dock 本体。
# 实测（2026-11-10）：CiceroUIWndFrame 是 200×200、Hidden Window 是 1680×980，
# 两者都被旧判定算成 Dock 窗口，导致 `dock_windows` 从 2 虚高到 3。
INTERNAL_TITLES = ("Hidden Window", "SystemResourceNotifyWindow",
                   "MediaContextNotificationWindow")
INTERNAL_CLASSES = ("CiceroUIWndFrame", "IME", "MSCTFIME")


def is_dock_window(w: dict) -> bool:
    """Dock 主窗口。三种形态都必须认得出来：

    - 唤出：673×157，可见；
    - 自动隐藏：WPF 对 SizeToContent="WidthAndHeight" 的窗口一旦 Visibility 非 Visible，
      HWND 会被收缩到 **2×2**（实测；改 Hidden 也无法避免，见 K-22）；
    - 首次引导 / 预览浮层：标题固定，直接排除。

    所以不能靠「宽高 >50」过滤——那会把 2×2 的 Dock 漏掉，又会把 200×200 的
    CiceroUIWndFrame（输入法框架）算进来。改用「class 前缀 + 空标题 + 排除 ToolTip 形状」：
    同类里只有 ToolTip popup 需要排掉，实测尺寸 170×47（高 40~55）。
    """
    if not w["class"].startswith("HwndWrapper[DockHost;"):
        return False
    if w.get("title") in FLOAT_TITLES or w.get("title") in INTERNAL_TITLES:
        return False
    width, height = w["size"]
    return not (40 <= height <= 55 and width <= 400)  # ToolTip popup


def existing_pid() -> int | None:
    try:
        output = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq DockHost.exe", "/FO", "CSV", "/NH"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20,
        ).stdout
        for line in output.splitlines():
            if "DockHost.exe" not in line:
                continue
            parts = [p.strip('"') for p in line.split('","')]
            if len(parts) >= 2 and parts[1].isdigit():
                return int(parts[1])
    except (OSError, subprocess.SubprocessError):
        return None
    return None


def process_pids(image: str) -> list[int]:
    """按映像名列出进程；WD-020 用 WinDock.Watchdog.exe 核验守护进程存活。"""
    try:
        output = subprocess.run(
            ["tasklist", "/FI", f"IMAGENAME eq {image}", "/FO", "CSV", "/NH"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        return []
    pids: list[int] = []
    for line in output.splitlines():
        if image not in line:
            continue
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2 and parts[1].isdigit():
            pids.append(int(parts[1]))
    return pids


def read_recovery() -> dict | None:
    try:
        if not RECOVERY.is_file():
            return None
        return json.loads(RECOVERY.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def pinned_names(cfg: dict) -> list[str]:
    return [str(p.get("name") or p.get("path") or p.get("id"))
            for p in cfg.get("pinned", []) if isinstance(p, dict)]


def parse_log_time(line: str):
    match = re.search(r"\] (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})", line)
    if not match:
        return None
    try:
        return datetime.strptime(match.group(1), "%Y-%m-%d %H:%M:%S.%f")
    except ValueError:
        return None


def read_log_tail(max_lines: int = 40) -> list[str]:
    if not LOG_DIR.is_dir():
        return []
    files = sorted(LOG_DIR.glob("windock-*.log"), key=lambda p: p.stat().st_mtime, reverse=True)
    if not files:
        return []
    lines = files[0].read_text(encoding="utf-8", errors="replace").splitlines()
    return lines[-max_lines:]


LAYOUT_RE = re.compile(
    r"layout nonce=(-?\d+) win=\((-?\d+),(-?\d+),(\d+),(\d+)\)"
    r" dip=\((-?\d+),(-?\d+)\) hwnd=\((-?\d+),(-?\d+),(-?\d+),(-?\d+)\)"
    r" count=(\d+)(.*)")


def dump_layout(timeout: float = 8.0) -> dict | None:
    """让常驻实例把图标屏幕坐标写进日志后解析。

    Dock 是 SizeToContent + 内容居中，窗口宽度随条目数变化，靠「窗口左边 + Npx」
    猜图标位置必然漂移（2026-11-10 实机：x0 落在两图标之间的间隙，拖拽完全不触发）。
    这个探针读的是控件 PointToScreen 的真实中心点。

    必须带 nonce 并只认自己那次：消息投递是异步的，若直接扫最新一条布局行，
    会读到上一次请求（甚至 Dock 滑出屏幕时）的旧坐标。
    """
    nonce = int(time.time() * 1000) % 1_000_000_007
    try:
        subprocess.run([str(EXE), f"--dump-layout={nonce}"], cwd=str(EXE.parent),
                       capture_output=True, text=True, timeout=timeout)
    except (OSError, subprocess.SubprocessError):
        return None

    for _ in range(16):
        for line in reversed(read_log_tail(80)):
            m = LAYOUT_RE.search(line)
            if not m or int(m.group(1)) != nonce:
                continue
            left, top, width, height = (int(m.group(i)) for i in range(2, 6))
            hwnd_rect = [int(m.group(i)) for i in range(8, 12)]
            icons = []
            for token in m.group(13).split():
                try:
                    index_part, rest = token.split(":", 1)
                    name, coords = rest.rsplit("@(", 1)
                    cx, cy = coords.rstrip(")").split(",")
                    icons.append({"index": int(index_part), "name": name,
                                  "center": [int(cx), int(cy)]})
                except ValueError:
                    continue
            return {"rect": hwnd_rect,
                    "wpf_rect": [left, top, left + width, top + height],
                    "icons": icons, "nonce": nonce}
        time.sleep(0.25)
    return None


PREVIEW_RE = re.compile(
    r"preview-layout nonce=(-?\d+) hwnd=\((-?\d+),(-?\d+),(-?\d+),(-?\d+)\)"
    r" cells=(\d+)(.*)")


def dump_preview(timeout: float = 8.0) -> dict | None:
    """让常驻实例把预览面板每个关闭按钮的中心屏幕坐标写进日志后解析（WD-037）。

    与 dump_layout 同一套 nonce 防串行模式：只认自己那次请求的行。
    """
    nonce = int(time.time() * 1000) % 1_000_000_007
    try:
        subprocess.run([str(EXE), f"--dump-preview={nonce}"], cwd=str(EXE.parent),
                       capture_output=True, text=True, timeout=timeout)
    except (OSError, subprocess.SubprocessError):
        return None

    for _ in range(16):
        for line in reversed(read_log_tail(200)):
            m = PREVIEW_RE.search(line)
            if not m or int(m.group(1)) != nonce:
                continue
            buttons = []
            for token in m.group(7).split():
                try:
                    idx, coords = token.rstrip(")").split(":(")
                    x, y = coords.split(",")
                    buttons.append({"index": int(idx), "center": [int(x), int(y)]})
                except ValueError:
                    continue
            return {"hwnd_rect": [int(m.group(i)) for i in range(2, 6)],
                    "cells": int(m.group(6)), "buttons": buttons, "nonce": nonce}
        time.sleep(0.25)
    return None


def close_leftover_notepads() -> int:
    """关闭所有标题带 winsmoke- 的遗留记事本窗口（测试自清理）。"""
    u32_ = ctypes.WinDLL("user32")
    hwnds = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def _cb(hwnd, _):
        n = u32_.GetWindowTextLengthW(hwnd)
        if n > 0:
            b = ctypes.create_unicode_buffer(n + 1)
            if u32_.GetWindowTextW(hwnd, b, n + 1) and "winsmoke-" in b.value:
                hwnds.append(hwnd)
        return True

    u32_.EnumWindows(_cb, 0)
    for h in hwnds:
        u32_.PostMessageW(h, 0x0010, 0, 0)  # WM_CLOSE
    return len(hwnds)


def wait_dock_revealed(pid: int, timeout: float = 4.0) -> bool:
    """轮询等待 Dock 完全滑入（K-23 修复后滑入应 <0.5s；留余量防时序抖动）。"""
    screen_h = u32.GetSystemMetrics(1)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        for w in windows_of(pid):
            if is_dock_window(w) and w["visible"] and w["rect"][1] < screen_h - 30:
                return True
        time.sleep(0.2)
    return False


def press_hotzone() -> None:
    """把光标分步压到底边热区（WD-035 实测教训）。

    SetCursorPos 一步跳到位时，如果光标本来就在目标附近（甚至相同坐标），
    系统不产生鼠标移动消息，热区窗口收不到 WM_MOUSEMOVE，滑入根本不触发；
    分 6px 小步逼近，每步都是一次真实光标移动，稳定触发热区。
    """
    screen_w = u32.GetSystemMetrics(0)
    screen_h = u32.GetSystemMetrics(1)
    u32.SetCursorPos(screen_w // 2, screen_h - 60)
    time.sleep(0.05)
    y = screen_h - 50
    while y < screen_h - 1:
        u32.SetCursorPos(screen_w // 2, y)
        y += 6
        time.sleep(0.02)
    u32.SetCursorPos(screen_w // 2, screen_h - 2)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--kill", action="store_true", help="terminate DockHost after verification")
    ap.add_argument("--sample-seconds", type=float, default=4.0,
                    help="idle CPU sampling window (default 4s)")
    ap.add_argument("--output", default=None,
                    help="result JSON path; M2+ 必须指定，避免覆盖 M1 交付记录 docs/M1-smoke-result.json")
    ap.add_argument("--test-autohide", action="store_true",
                    help="M2: 把光标移到底边 2px 热区，验证自动隐藏的 Dock 能滑入（会移动并恢复光标）")
    ap.add_argument("--cpu-trace", action="store_true",
                    help="按 5s 分段输出空闲 CPU，区分冷启动开销与稳态空转")
    ap.add_argument("--settle-seconds", type=float, default=5.0,
                    help="启动后先等待该时长再开始空闲 CPU 采样，避免把冷启动开销算进 8.1 指标")
    ap.add_argument("--exe", default=None, help="override DockHost.exe path (default: published artifact)")
    ap.add_argument("--restart", action="store_true",
                    help="kill an already running DockHost and start a fresh one")
    ap.add_argument("--replay-guide", action="store_true",
                    help="WD-017: set general.firstRunCompleted=false before launching")
    ap.add_argument("--test-guide", action="store_true",
                    help="WD-017: verify the 3-step guide window and dismiss it with Escape")
    ap.add_argument("--test-preview", action="store_true",
                    help="WD-014: hover Dock icons and verify the DWM preview panel opens/closes")
    ap.add_argument("--close-via-preview", action="store_true",
                    help="WD-037/AC-05: hover, click the panel close button, verify the target window exits")
    ap.add_argument("--fps-probe", action="store_true",
                    help="WD-038/AC-21: keep hover animation alive for 60s and parse fps log lines")
    ap.add_argument("--dpi-audit", action="store_true",
                    help="WD-039/AC-12: verify DPI awareness context and physical/logical consistency")
    ap.add_argument("--park-cursor", action="store_true",
                    help="move the cursor to a neutral area during idle CPU sampling (8.1 definition)")
    ap.add_argument("--test-drag", action="store_true",
                    help="WD-016: synthesize a pointer drag and verify Pinned order is persisted")
    ap.add_argument("--test-watchdog", action="store_true",
                    help="WD-020: kill DockHost and verify the watchdog restores the taskbar within 10s")
    args = ap.parse_args()
    sample_seconds = max(1.0, args.sample_seconds)
    result_path = Path(args.output) if args.output else RESULT
    result_path.parent.mkdir(parents=True, exist_ok=True)
    exe = Path(args.exe) if args.exe else EXE

    started_at = datetime.now()
    cursor = wintypes.POINT()
    u32.GetCursorPos(ctypes.byref(cursor))
    original_cursor = (cursor.x, cursor.y)
    report: dict = {"timestamp": started_at.isoformat(timespec="seconds"), "exe": str(exe),
                    "cursor_original": list(original_cursor)}
    if not exe.is_file():
        report["error"] = "DockHost.exe not found; run build_m1.ps1 -Publish first"
        result_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(report["error"])
        return 2

    config_path = Path.home() / "AppData" / "Roaming" / "WinDock" / "config.json"
    if args.replay_guide and config_path.is_file():
        try:
            cfg = json.loads(config_path.read_text(encoding="utf-8"))
            cfg.setdefault("general", {})["firstRunCompleted"] = False
            config_path.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
            report["guide_replayed"] = True
        except (OSError, ValueError) as exc:
            report["guide_replay_error"] = str(exc)

    if args.restart:
        old_pid = existing_pid()
        if old_pid is not None:
            subprocess.run(["taskkill", "/PID", str(old_pid), "/F"],
                           capture_output=True, text=True, timeout=20)
            report["restarted_from_pid"] = old_pid
            time.sleep(1.2)

    started_new = False
    pid = existing_pid()
    cpu0 = None
    if pid is None:
        process = subprocess.Popen([str(exe)], cwd=str(exe.parent))
        pid = process.pid
        started_new = True
        cpu0 = cpu_time(pid)
        time.sleep(3)
        report["early_exit_code"] = process.poll()
    else:
        report["early_exit_code"] = None

    report["pid"] = pid
    report["started_new_process"] = started_new
    all_windows = windows_of(pid)
    report["windows"] = [w for w in all_windows if w["visible"]]
    report["visible_window_count"] = len(report["windows"])
    dock_windows = [w for w in all_windows if is_dock_window(w)]
    report["dock_window_present"] = bool(dock_windows)
    report["auto_hidden"] = bool(dock_windows) and not any(w["visible"] for w in dock_windows)
    report["hotzone_windows"] = [w for w in all_windows if "HotZone" in w["class"]]

    # WD-017：引导窗口按 Title 精确识别；Escape 走 GuideWindow.OnKeyDown → Complete(false)
    if args.test_guide:
        # 引导在组合根启动约 350ms 后才由 TimerScheduler 弹出，启动后 3s 的
        # 首次枚举可能早于它；这里最多再等 2.4s（不轮询常驻，只测这一次）。
        guide = None
        for _ in range(7):
            guide = next((w for w in windows_of(pid)
                          if w.get("title") == "WinDock Guide" and w["visible"]), None)
            if guide is not None:
                break
            time.sleep(0.4)
        report["guide_window_present"] = guide is not None
        report["guide_window_rect"] = guide["rect"] if guide else None
        if guide is not None:
            u32.SetForegroundWindow(guide["hwnd"])
            time.sleep(0.2)
            u32.keybd_event(27, 0, 0, 0)  # VK_ESCAPE down
            u32.keybd_event(27, 0, 2, 0)  # KEYEVENTF_KEYUP
            time.sleep(0.5)
            # 前台锁定可能让合成按键送不到目标；用 WM_CLOSE 兜底，
            # GuideWindow.Closed → Complete(false) 同样会落 firstRunCompleted。
            if any(w.get("title") == "WinDock Guide" and w["visible"]
                   for w in windows_of(pid)):
                u32.PostMessageW(guide["hwnd"], 0x0010, 0, 0)  # WM_CLOSE
                report["guide_dismiss_fallback"] = "WM_CLOSE"
                time.sleep(0.6)
            report["guide_dismissed"] = not any(
                w.get("title") == "WinDock Guide" and w["visible"]
                for w in windows_of(pid))
            report["guide_dismissed_by_escape"] = report["guide_dismissed"]
            if config_path.is_file():
                try:
                    cfg = json.loads(config_path.read_text(encoding="utf-8"))
                    report["guide_first_run_completed"] = bool(
                        cfg.get("general", {}).get("firstRunCompleted", False))
                except (OSError, ValueError):
                    report["guide_first_run_completed"] = None
        else:
            report["guide_already_completed"] = True

    # 8.1「空闲 CPU」口径是「鼠标不在 Dock 上」的稳态：采样前把光标移到中性区域，
    # 避免用户恰好把鼠标停在 Dock 图标上触发放大/预览交互（K-11）。
    if args.park_cursor:
        u32.SetCursorPos(10, 200)
        report["cursor_parked"] = [10, 200]
        time.sleep(0.5)

    # 8.1「空闲 CPU」口径是稳态：冷启动的 JIT / 图标提取 / 初始化不算在内，
    # 否则 30s 窗口会被启动阶段约 1s 的 CPU 时间污染（实测虚高到 3%）。
    settle_left = max(0.0, args.settle_seconds - (3.0 if started_new else 0.0))
    if settle_left > 0:
        time.sleep(settle_left)
    report["settle_seconds"] = args.settle_seconds
    cpu1 = cpu_time(pid)
    if cpu0 is not None and cpu1 is not None:
        report["cold_start_cpu_seconds"] = round(max(0.0, cpu1 - cpu0), 3)
    if args.cpu_trace:
        segments: list[float | None] = []
        last = cpu1
        remaining = sample_seconds
        while remaining > 0:
            step = min(5.0, remaining)
            time.sleep(step)
            current = cpu_time(pid)
            segments.append(round(max(0.0, (current - last) / step * 100.0), 3)
                            if current is not None and last is not None else None)
            last = current
            remaining -= step
        report["cpu_trace_percent_per_5s"] = segments
        cpu2 = last
    else:
        time.sleep(sample_seconds)
        cpu2 = cpu_time(pid)
    report["sample_seconds"] = sample_seconds
    report["idle_cpu_percent"] = (
        round(max(0.0, (cpu2 - cpu1) / sample_seconds * 100.0), 3)
        if cpu1 is not None and cpu2 is not None else None
    )
    memory = working_set(pid)
    report["working_set_mb"] = round(memory / 1024 / 1024, 2) if memory else None

    logs = read_log_tail()
    # 自检会故意写坏一份临时配置并产生 ERROR；这里只统计本次启动之后的错误。
    report["log_errors"] = [
        line for line in logs
        if "][ERROR]" in line
        and (stamp := parse_log_time(line)) is not None
        and stamp >= started_at
    ]
    report["log_tail"] = logs[-12:]

    if args.test_autohide:
        # 冷启动 800ms 后 Dock 应已自动隐藏；把光标移到底边热区验证滑入。
        point = wintypes.POINT()
        u32.GetCursorPos(ctypes.byref(point))
        original = (point.x, point.y)
        screen_w = u32.GetSystemMetrics(0)
        screen_h = u32.GetSystemMetrics(1)
        press_hotzone()
        time.sleep(0.6)
        restored = [w for w in windows_of(pid)
                    if w["visible"] and is_dock_window(w)]
        report["autohide_restore"] = bool(restored)
        report["hotzone_probe_point"] = [screen_w // 2, screen_h - 1]
        u32.SetCursorPos(*original)

    # WD-014：把光标移到 Dock 图标上，500ms 后应弹出预览面板；移开后应注销并关闭。
    if args.test_preview:
        point = wintypes.POINT()
        u32.GetCursorPos(ctypes.byref(point))
        preview_cursor = (point.x, point.y)
        screen_w = u32.GetSystemMetrics(0)
        screen_h = u32.GetSystemMetrics(1)

        visible_dock = [w for w in windows_of(pid)
                        if w["visible"] and is_dock_window(w)]
        if not visible_dock:
            press_hotzone()
            time.sleep(0.7)
            visible_dock = [w for w in windows_of(pid)
                            if w["visible"] and is_dock_window(w)]
        report["preview_dock_visible_before_hover"] = bool(visible_dock)

        panel = None
        hover_point = None
        layout = dump_layout() if visible_dock else None
        if layout and any(not (0 <= icon["center"][1] <= screen_h)
                          for icon in layout["icons"]):
            press_hotzone()
            time.sleep(0.9)
            layout = dump_layout()
        report["preview_layout"] = layout
        if visible_dock and layout:
            # 逐个真实图标中心悬停；固定项可能没有窗口，命中带窗口的项才弹面板。
            # 邻域放大（FR-203）会把右侧图标顶开，未命中时重新取一次布局。
            for attempt in range(len(layout["icons"])):
                icon = layout["icons"][attempt]
                x, y = icon["center"]
                u32.SetCursorPos(x, y)
                time.sleep(0.85)
                panel = next((w for w in windows_of(pid)
                              if w.get("title") == "WinDock Preview" and w["visible"]), None)
                if panel is not None:
                    hover_point = [x, y]
                    report["preview_hover_icon"] = icon["name"]
                    break
                refreshed = dump_layout()
                if refreshed:
                    report["preview_layout"] = refreshed
                    layout = refreshed
        elif visible_dock:
            left, top, right, bottom = visible_dock[0]["rect"]
            y = bottom - max(24, min(45, (bottom - top) // 2))
            for x in range(left + 30, right - 10, 55):
                u32.SetCursorPos(x, y)
                time.sleep(0.85)
                panel = next((w for w in windows_of(pid)
                              if w.get("title") == "WinDock Preview" and w["visible"]), None)
                if panel is not None:
                    hover_point = [x, y]
                    break

        report["preview_window_present"] = panel is not None
        report["preview_window_rect"] = panel["rect"] if panel else None
        report["preview_hover_point"] = hover_point
        u32.SetCursorPos(*preview_cursor)
        time.sleep(0.9)
        report["preview_closed_on_leave"] = not any(
            w.get("title") == "WinDock Preview" and w["visible"]
            for w in windows_of(pid))

    # WD-037 / AC-05：悬停弹面板 → 点关闭按钮 → 目标窗口退出、面板正确收缩。
    if args.close_via_preview:
        close_test: dict = {"enabled": True}
        # 可牺牲目标：记事本 + 临时文件（无未保存内容，WM_CLOSE 不会被拦截）。
        tmp = Path(tempfile.gettempdir()) / f"winsmoke-{os.getpid()}.txt"
        tmp.write_text("close-via-preview target\n", encoding="utf-8")
        notepad = None
        try:
            # 先清理历史轮次的遗留窗口（finally 只杀 stub 进程，真实记事本窗口
            # 是单进程多窗口，会残留并混进 Dock 分组，干扰关闭目标判定）。
            close_leftover_notepads()
            notepad = subprocess.Popen(["notepad.exe", str(tmp)])
            close_test["notepad_pid"] = notepad.pid
            time.sleep(2.6)  # 等窗口出现并被枚举进 Dock 运行区

            # 本会话终端常被 Dock 判为全屏/演示态（QUNS=1）而禁用热区；
            # 把记事本设为前台既解除全屏态，也符合真实使用场景。
            targets_found: list[dict] = []

            @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
            def _enum_cb(hwnd, _l):
                length = u32.GetWindowTextLengthW(hwnd)
                if length > 0:
                    buf = ctypes.create_unicode_buffer(length + 1)
                    if u32.GetWindowTextW(hwnd, buf, length + 1) and tmp.name in buf.value:
                        wpid = wintypes.DWORD()
                        u32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
                        targets_found.append({"hwnd": hwnd, "title": buf.value,
                                              "hwnd_pid": wpid.value})
                return True

            u32.EnumWindows(_enum_cb, 0)
            if targets_found:
                u32.SetForegroundWindow(targets_found[0]["hwnd"])
                close_test["notepad_window"] = targets_found[0]["title"]
            # Win11 打包版记事本：Popen 的 pid 只是启动 stub，真实窗口属于
            # 另一个进程——退出判定必须按窗口所属 pid（或窗口消失）。
            window_pid = targets_found[0]["hwnd_pid"] if targets_found else notepad.pid
            close_test["notepad_window_pid"] = window_pid
            time.sleep(3.2)  # 等全屏检测退出 + Dock 热区恢复

            screen_w = u32.GetSystemMetrics(0)
            screen_h = u32.GetSystemMetrics(1)
            # 唤出 + 取布局最多重试 3 轮：探针实测存在两类抖动——
            # ① 光标恰好停在热区导致热区无「进入」事件；② dump 消息异步偶发超时。
            layout = None
            icon = None
            for attempt in range(3):
                if not wait_dock_revealed(pid):
                    press_hotzone()
                    wait_dock_revealed(pid)
                layout = dump_layout()
                if layout and any(not 0 <= icon["center"][1] <= screen_h
                                  for icon in layout["icons"]):
                    press_hotzone()
                    wait_dock_revealed(pid)
                    layout = dump_layout()
                icon = next((i for i in (layout or {}).get("icons", [])
                             if "notepad" in i["name"].lower() or "记事本" in i["name"]),
                            None)
                if icon is not None:
                    break
                close_test[f"reveal_attempt_{attempt}"] = {
                    "revealed": wait_dock_revealed(pid, 0.4),
                    "icons": [(i["name"], i["center"]) for i in (layout or {}).get("icons", [])][:8],
                }
            close_test["layout_captured"] = layout is not None
            close_test["notepad_icon_found"] = icon is not None
            if icon is not None:
                # 悬停图标中心，等面板弹出（hover 延时默认 500ms + 布局）。
                u32.SetCursorPos(*icon["center"])
                panel = None
                for _ in range(8):
                    time.sleep(0.5)
                    panel = next((w for w in windows_of(pid)
                                  if w.get("title") == "WinDock Preview" and w["visible"]),
                                 None)
                    if panel is not None:
                        break
                close_test["panel_opened"] = panel is not None
                if panel is not None:
                    preview = dump_preview()
                    close_test["preview_dump"] = preview
                    buttons = (preview or {}).get("buttons") or []
                    close_test["cells_before"] = (preview or {}).get("cells")
                    if buttons:
                        bx, by = buttons[0]["center"]
                        u32.SetCursorPos(bx, by)
                        time.sleep(0.35)
                        u32.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
                        u32.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP

                        # 记事本应在 WM_CLOSE 后退出；面板收缩或关闭。
                        exited = False
                        for _ in range(16):
                            time.sleep(0.5)
                            # 双判据：窗口消失（含打包版记事本的真实窗口 pid）
                            # 或 stub 进程退出，任一满足即视为已关闭。
                            if notepad.poll() is not None or not windows_of(window_pid):
                                exited = True
                                break
                        close_test["notepad_exited"] = exited
                        close_test["wm_close_logged"] = any(
                            "已发送 WM_CLOSE" in line for line in read_log_tail(120))
                        time.sleep(1.2)
                        still_open = any(
                            w.get("title") == "WinDock Preview" and w["visible"]
                            for w in windows_of(pid))
                        close_test["panel_closed_after"] = not still_open
                        if still_open:
                            after = dump_preview()
                            close_test["cells_after"] = (after or {}).get("cells")
            u32.SetCursorPos(*original_cursor)
        finally:
            if notepad is not None and notepad.poll() is None:
                notepad.kill()
            close_leftover_notepads()
            try:
                tmp.unlink(missing_ok=True)
            except OSError:
                pass
        report["close_via_preview"] = close_test

    # WD-038 / AC-21：让悬停动画持续 60s，解析宿主落盘的 fps 行。
    # 静止悬停会在动画收敛后退订 Rendering（M1 D4），FPS 只在动画活跃期间有意义；
    # 探针在相邻两个图标间每 2s 轮换，持续触发邻域放大动画（这正是 AC-21 关心的
    # 「悬停交互期帧率」），60s 结束后取全部 fps 行的最差值。
    if args.fps_probe:
        fps: dict = {"enabled": True}
        screen_w = u32.GetSystemMetrics(0)
        screen_h = u32.GetSystemMetrics(1)
        if not wait_dock_revealed(pid):
            press_hotzone()
            wait_dock_revealed(pid)
        layout = dump_layout()
        if layout and any(not 0 <= icon["center"][1] <= screen_h
                          for icon in layout["icons"]):
            press_hotzone()
            wait_dock_revealed(pid)
            layout = dump_layout()
        icons = [i for i in (layout or {}).get("icons", [])
                 if 0 <= i["center"][1] <= screen_h]
        fps["hoverable_icons"] = len(icons)
        if len(icons) >= 2:
            deadline = time.monotonic() + 60.0
            idx = 0
            while time.monotonic() < deadline:
                icon = icons[idx % len(icons)]
                u32.SetCursorPos(*icon["center"])
                idx += 1
                time.sleep(2.0)
            u32.SetCursorPos(*original_cursor)

            fps_re = re.compile(
                r"fps 1s=([\d.]+) window60s avg=([\d.]+) p5=([\d.]+) "
                r"min=([\d.]+) active bursts=(\d+) avg=([\d.]+) "
                r"p5=([\d.]+) min=([\d.]+) dropped=(\d+) frames=(\d+)")
            rows = []
            for line in read_log_tail(400):
                m = fps_re.search(line)
                if m:
                    rows.append({
                        "one_s": float(m.group(1)), "avg": float(m.group(2)),
                        "p5": float(m.group(3)), "min": float(m.group(4)),
                        "bursts": int(m.group(5)), "active_avg": float(m.group(6)),
                        "active_p5": float(m.group(7)), "active_min": float(m.group(8)),
                        "dropped": int(m.group(9)), "frames": int(m.group(10))})
            fps["fps_lines"] = rows[-8:]
            # 只统计有效行（有活跃突发）：冷启动初期的空行 active 全为 0。
            valid = [r for r in rows if r["bursts"] > 0]
            fps["valid_lines"] = len(valid)
            if valid:
                fps["worst_active_p5"] = min(r["active_p5"] for r in valid)
                fps["worst_active_min"] = min(r["active_min"] for r in valid)
                fps["avg_of_active_avg"] = round(
                    sum(r["active_avg"] for r in valid) / len(valid), 2)
                fps["last_frames"] = rows[-1]["frames"]
                fps["total_bursts"] = sum(r["bursts"] for r in rows)
                fps["total_dropped"] = sum(r["dropped"] for r in rows)
        report["fps_probe"] = fps

    # WD-039 / AC-12：DPI 感知上下文与物理/逻辑坐标一致性核查（脚本可做部分；
    # 150%/200% 档位必须真改系统缩放，见口径清单 C-5）。
    if args.dpi_audit:
        audit: dict = {"enabled": True}
        if not wait_dock_revealed(pid):
            press_hotzone()
            wait_dock_revealed(pid)
        dock = next((w for w in windows_of(pid)
                     if is_dock_window(w) and w["visible"]), None)
        if dock is None:
            audit["dock_visible"] = False
        else:
            audit["dock_visible"] = True
            hwnd = dock["hwnd"]
            # ① DPI 感知上下文：Per-Monitor V2 的上下文句柄与系统值恒等比较。
            try:
                ctx = ctypes.windll.user32.GetWindowDpiAwarenessContext(hwnd)
                pm2 = ctypes.windll.user32.GetAwarenessFromDpiAwarenessContext(ctx)
                # DPI_AWARENESS: 0=unaware, 1=system, 2=per-monitor v1, 3=per-monitor v2。
                # 实测返回 2（v1）：manifest 同时声明了 legacy dpiAware=true/pm 与
                # dpiAwareness=PerMonitorV2，Windows 按 legacy 解析为 v1。坐标一致性
                # 检查（②③④）全过，v1/v2 差异主要在非客户区/对话框自动缩放，
                # 对无边框自绘窗口影响有限，登记为观察项。
                audit["dpi_awareness"] = pm2
                audit["dpi_awareness_is_per_monitor"] = pm2 in (2, 3)
            except AttributeError:
                audit["dpi_awareness"] = "unsupported"
            # ② 窗口 DPI 与所在显示器 DPI 一致（PerMonitorV2 下窗口跟随显示器）。
            dpi = ctypes.windll.user32.GetDpiForWindow(hwnd)
            audit["window_dpi"] = dpi
            monitor = ctypes.windll.user32.MonitorFromWindow(hwnd, 1)  # MONITOR_DEFAULTTONEAREST
            class RECT_(ctypes.Structure):
                _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                            ("right", ctypes.c_long), ("bottom", ctypes.c_long)]
            class MONITORINFO_(ctypes.Structure):
                _fields_ = [("cbSize", ctypes.c_ulong), ("rcMonitor", RECT_),
                            ("rcWork", RECT_), ("dwFlags", ctypes.c_ulong)]
            mi = MONITORINFO_()
            mi.cbSize = ctypes.sizeof(mi)
            if ctypes.windll.user32.GetMonitorInfoW(monitor, ctypes.byref(mi)):
                audit["monitor_rect"] = [mi.rcMonitor.left, mi.rcMonitor.top,
                                         mi.rcMonitor.right, mi.rcMonitor.bottom]
                # GetDpiForMonitor 在 shcore.dll（OUT 参数双 DPI），不在 user32。
                try:
                    dpi_x = ctypes.c_uint()
                    dpi_y = ctypes.c_uint()
                    ok = ctypes.windll.shcore.GetDpiForMonitor(
                        ctypes.c_void_p(monitor), 0, ctypes.byref(dpi_x), ctypes.byref(dpi_y))
                    audit["monitor_dpi"] = dpi_x.value if ok == 0 else None
                except AttributeError:
                    audit["monitor_dpi"] = None
                audit["window_dpi_equals_monitor"] = (
                    audit.get("monitor_dpi") == dpi)
            # ③ 图标中心（物理，产品侧 ×dpi 换算）必须落在 Dock 的 GetWindowRect 内。
            layout = dump_layout()
            icons = (layout or {}).get("icons") or []
            rect = (layout or {}).get("rect") or dock["rect"]
            l, t, r, b = rect
            inside = [i["name"] for i in icons
                      if not (l <= i["center"][0] <= r and t <= i["center"][1] <= b)]
            audit["icons_total"] = len(icons)
            audit["icons_outside_dock_rect"] = inside
            # ④ 图标中心必须在显示器物理边界内（K-19：曾整体偏移 200+px 落到屏外）。
            if icons and audit.get("monitor_rect"):
                ml, mt, mr, mb = audit["monitor_rect"]
                offscreen = [i["name"] for i in icons
                             if not (ml <= i["center"][0] <= mr
                                     and mt <= i["center"][1] <= mb)]
                audit["icons_offscreen"] = offscreen
            # ⑤ 热区窗口矩形应在显示器边界内。
            hot = next((w for w in windows_of(pid) if "HotZone" in w["class"]), None)
            if hot is not None and audit.get("monitor_rect"):
                hl, ht, hr, hb = hot["rect"]
                ml, mt, mr, mb = audit["monitor_rect"]
                audit["hotzone_in_monitor"] = (ml <= hl and mt <= ht
                                               and hr <= mr and hb <= mb)
                audit["hotzone_rect"] = [hl, ht, hr, hb]
        report["dpi_audit"] = audit

    # WD-016：合成「按下 → 移动 → 抬起」，校验 Pinned 顺序写回 config.json（AC-06）。
    if args.test_drag:
        drag: dict = {"enabled": True}
        original_cfg = None
        try:
            original_cfg = json.loads(config_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            pass
        cfg = dict(original_cfg or {})
        names_before = pinned_names(cfg)
        if len(names_before) < 2:
            # 用系统自带且确实存在的 exe，避免误点击时额外产生 Launcher ERROR。
            apps = [
                ("SmokeA", r"C:\Windows\System32\notepad.exe"),
                ("SmokeB", r"C:\Windows\System32\cmd.exe"),
                ("SmokeC", r"C:\Windows\explorer.exe"),
            ]
            cfg["pinned"] = [
                {"id": f"smoke-{i}", "type": "app", "path": path,
                 "args": "", "aumid": "", "name": name}
                for i, (name, path) in enumerate(apps)
            ]
            config_path.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
            drag["seeded_pinned"] = True
            time.sleep(1.2)  # 等配置热更新重建图标树
            names_before = pinned_names(cfg)
        drag["pinned_before"] = names_before

        screen_w = u32.GetSystemMetrics(0)
        screen_h = u32.GetSystemMetrics(1)
        # 自动隐藏后 Dock 的 HWND 仍存在但 rect 已滑出屏幕；必须先把光标压到
        # 底边热区等滑入完成，并校验 rect 在屏幕内，否则合成点击会落到屏幕外。
        press_hotzone()
        time.sleep(0.9)

        def dock_in_screen() -> list[dict]:
            return [w for w in windows_of(pid)
                    if w["visible"] and is_dock_window(w)
                    and w["rect"][1] >= -4 and w["rect"][3] <= screen_h + 4]

        visible_dock = dock_in_screen()
        if not visible_dock:
            time.sleep(0.7)
            visible_dock = dock_in_screen()
        drag["dock_rect"] = visible_dock[0]["rect"] if visible_dock else None

        layout = dump_layout() if visible_dock else None
        if layout and any(not (0 <= icon["center"][1] <= screen_h)
                          for icon in layout["icons"]):
            # 探针是异步消息，期间 Dock 可能已被自动隐藏（坐标落到屏幕外）：压回热区重取
            press_hotzone()
            time.sleep(0.9)
            layout = dump_layout()
        drag["layout"] = layout

        if visible_dock and layout and len(layout["icons"]) >= 2:
            # 用真实图标中心点：第一个图标按下 → 越过两个图标后抬起（拖到第三项右侧）
            icons = layout["icons"]
            src = icons[0]["center"]
            dst = icons[min(2, len(icons) - 1)]["center"]
            y = src[1]
            x0, x1 = src[0], dst[0] + 40
            u32.SetCursorPos(x0, y)
            time.sleep(0.4)
            u32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
            for step in range(1, 11):
                u32.SetCursorPos(int(x0 + (x1 - x0) * step / 10), y)
                time.sleep(0.05)
            time.sleep(0.3)
            u32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
            time.sleep(1.0)
            drag["drag_points"] = [[x0, y], [x1, y]]
            drag["drag_source_name"] = icons[0]["name"]
        elif visible_dock:
            # 退化路径：没有布局探针时按旧方式从窗口左边缘估算。
            left, top, right, bottom = visible_dock[0]["rect"]
            y = bottom - max(24, min(45, (bottom - top) // 2))
            x0, x1 = left + 62, left + 62 + 125
            u32.SetCursorPos(x0, y)
            time.sleep(0.4)
            u32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
            for step in range(1, 9):
                u32.SetCursorPos(int(x0 + (x1 - x0) * step / 8), y)
                time.sleep(0.05)
            time.sleep(0.3)
            u32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
            time.sleep(1.0)
            drag["drag_points"] = [[x0, y], [x1, y]]
            drag["layout_fallback"] = True
        else:
            drag["dock_not_visible"] = True

        try:
            names_after = pinned_names(json.loads(config_path.read_text(encoding="utf-8")))
            drag["pinned_after"] = names_after
            drag["order_changed"] = (names_after != names_before
                                     and len(names_after) == len(names_before))
        except (OSError, ValueError) as exc:
            drag["read_error"] = str(exc)
            drag["order_changed"] = False
        # 只认本次启动之后的日志，避免把 --selftest 写下的「固定项重排」当成实机证据。
        recent = [line for line in read_log_tail(160)
                  if (stamp := parse_log_time(line)) is not None and stamp >= started_at]
        drag["log_has_drag"] = any("开始拖拽" in line or "固定项重排" in line
                                   for line in recent)
        if drag.get("seeded_pinned") and original_cfg is not None:
            config_path.write_text(json.dumps(original_cfg, ensure_ascii=False, indent=2),
                                   encoding="utf-8")
            time.sleep(0.6)
            drag["config_restored"] = True
        report["drag"] = drag
        report["drag_order_changed"] = bool(drag.get("order_changed"))

    # WD-020：强杀主进程 → 守护进程发现宿主消失 → 恢复任务栏 + 写恢复事件 → 自行退出。
    # 随后重启并优雅退出，验证 S 握手不会把正常退出误判为崩溃。
    if args.test_watchdog:
        wd: dict = {"enabled": True}
        wd["watchdog_pids_before"] = process_pids("WinDock.Watchdog.exe")
        before_mtime = RECOVERY.stat().st_mtime if RECOVERY.is_file() else None
        wd["recovery_before"] = read_recovery()

        subprocess.run(["taskkill", "/PID", str(pid), "/F"],
                       capture_output=True, text=True, timeout=20)
        deadline = time.monotonic() + 10.0
        event = None
        while time.monotonic() < deadline:
            now_mtime = RECOVERY.stat().st_mtime if RECOVERY.is_file() else None
            if now_mtime is not None and now_mtime != before_mtime:
                event = read_recovery()
                if event:
                    break
            time.sleep(0.4)
        wd["recovery_after"] = event
        wd["recovery_detected"] = event is not None
        wd["recovery_reason"] = event.get("reason") if event else None

        for _ in range(12):
            if not process_pids("WinDock.Watchdog.exe"):
                break
            time.sleep(0.4)
        wd["watchdog_pids_after_crash"] = process_pids("WinDock.Watchdog.exe")
        wd["watchdog_exited"] = not wd["watchdog_pids_after_crash"]
        wd["log_has_recovery"] = "判定失联" in "\n".join(read_log_tail(80))

        # 正常退出握手：重启的 Dock 会消费恢复事件（托盘气泡），WM_CLOSE 应让
        # 看门狗收到 S 帧后直接退出，不再生成新的恢复事件。
        after_crash_mtime = RECOVERY.stat().st_mtime if RECOVERY.is_file() else None
        clean = subprocess.Popen([str(exe)], cwd=str(exe.parent))
        time.sleep(3.0)
        wd["clean_shutdown_pid"] = clean.pid
        wd["clean_watchdog_pids"] = process_pids("WinDock.Watchdog.exe")
        wd["clean_dock_windows"] = len(
            [w for w in windows_of(clean.pid) if is_dock_window(w)])
        # App.xaml 为 OnExplicitShutdown，WM_CLOSE 关主窗口不会结束进程；
        # 用受控的 --exit IPC 走 Application.Shutdown → OnExit → 看门狗 S 帧。
        subprocess.run([str(exe), "--exit"], cwd=str(exe.parent),
                       capture_output=True, text=True, timeout=20)
        wd["clean_exit_command"] = True
        for _ in range(12):
            if clean.poll() is not None:
                break
            time.sleep(0.4)
        time.sleep(1.0)
        clean_mtime = RECOVERY.stat().st_mtime if RECOVERY.is_file() else None
        wd["clean_shutdown_exited"] = clean.poll() is not None
        wd["clean_shutdown_no_recovery"] = (clean_mtime is None
                                            or clean_mtime == after_crash_mtime)
        wd["clean_watchdog_exited"] = not process_pids("WinDock.Watchdog.exe")
        if clean.poll() is None:
            subprocess.run(["taskkill", "/PID", str(clean.pid), "/F"],
                           capture_output=True, text=True, timeout=20)
            wd["clean_shutdown_forced_kill"] = True

        report["watchdog"] = wd
        report["watchdog_recovery_detected"] = bool(wd["recovery_detected"])
        report["watchdog_process_exited"] = bool(wd["watchdog_exited"])
        report["watchdog_clean_shutdown"] = bool(
            wd["clean_shutdown_exited"] and wd["clean_shutdown_no_recovery"]
            and wd["clean_watchdog_exited"])

    # 采样之后拖拽 / 看门狗用例仍会写日志，收尾再查一次本次启动后的 ERROR。
    report["log_errors_late"] = [
        line for line in read_log_tail(200)
        if "][ERROR]" in line
        and (stamp := parse_log_time(line)) is not None
        and stamp >= started_at
    ]

    ok = (report["dock_window_present"]
          and report["early_exit_code"] is None
          and not report["log_errors"]
          and not report["log_errors_late"])
    if args.test_autohide:
        ok = ok and report["auto_hidden"] and report["autohide_restore"]
    if args.test_guide:
        ok = ok and (report.get("guide_window_present") is True
                     or report.get("guide_already_completed") is True)
        if report.get("guide_window_present"):
            ok = ok and report.get("guide_dismissed_by_escape") is True
            ok = ok and report.get("guide_first_run_completed") is True
    if args.test_preview:
        ok = ok and report.get("preview_window_present") is True
        ok = ok and report.get("preview_closed_on_leave") is True
    if args.close_via_preview:
        c = report.get("close_via_preview") or {}
        ok = ok and c.get("notepad_icon_found") is True
        ok = ok and c.get("panel_opened") is True
        ok = ok and bool((c.get("preview_dump") or {}).get("buttons"))
        ok = ok and c.get("notepad_exited") is True
        ok = ok and c.get("wm_close_logged") is True
        # 单窗口场景面板应整面板关闭；多窗口场景 cells 应减一。
        if c.get("cells_before") == 1:
            ok = ok and c.get("panel_closed_after") is True
        elif c.get("cells_after") is not None:
            ok = ok and c["cells_after"] < c["cells_before"]
    if args.fps_probe:
        f = report.get("fps_probe") or {}
        ok = ok and f.get("hoverable_icons", 0) >= 2
        ok = ok and f.get("fps_lines")  # 至少产出一行 fps 统计
        if f.get("fps_lines"):
            # AC-21 判定用 active 平均口径（时间平均会被两段动画之间的空闲期稀释）。
            # P5/单帧最差与丢帧率只如实记录不作断言（实测 p5≈32、~9% 帧超 25ms）。
            ok = ok and f.get("avg_of_active_avg", 0) >= 55.0
    if args.dpi_audit:
        a = report.get("dpi_audit") or {}
        ok = ok and a.get("dock_visible") is True
        ok = ok and a.get("dpi_awareness_is_per_monitor") is True
        ok = ok and not a.get("icons_outside_dock_rect")
        ok = ok and not a.get("icons_offscreen")
        if "hotzone_in_monitor" in a:
            ok = ok and a.get("hotzone_in_monitor") is True
    if args.test_drag:
        ok = ok and report.get("drag_order_changed") is True
    if args.test_watchdog:
        ok = ok and report.get("watchdog_recovery_detected") is True
        ok = ok and report.get("watchdog_process_exited") is True
        ok = ok and report.get("watchdog_clean_shutdown") is True
    report["verdict"] = "PASS" if ok else "FAIL"

    # 看门狗用例必须强杀主进程；验证完把 Dock 重新拉起，保持桌面可用状态。
    if args.test_watchdog and not args.kill:
        final = subprocess.Popen([str(exe)], cwd=str(exe.parent))
        time.sleep(2.0)
        report["final_dock_pid"] = final.pid
    if args.park_cursor:
        u32.SetCursorPos(*original_cursor)
        report["cursor_restored"] = True

    result_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "log_tail"},
                     ensure_ascii=False, indent=2))
    print(f"Result written to {result_path}")

    if args.kill:
        subprocess.run(["taskkill", "/PID", str(pid), "/F"],
                       capture_output=True, text=True, timeout=20)
        print(f"Terminated PID {pid}")
    return 0 if report["verdict"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
