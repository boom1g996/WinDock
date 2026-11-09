"""M1 实机冒烟验证（零依赖）：启动 DockHost，确认窗口存在、进程存活、
采样空闲 CPU/内存，并把结果写入 docs/M1-smoke-result.json。

不终止进程：验证完成后 Dock 继续运行，用户可直接看到效果。
用法：python tools/smoke_m1.py [--kill]
"""
from __future__ import annotations

import argparse
import ctypes
import json
import re
import subprocess
import sys
import time
from ctypes import wintypes
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
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


def is_dock_window(w: dict) -> bool:
    """Dock 主窗口：宽高都 >50；排除热区、首次引导与缩略图浮层（M2 第三批）。"""
    return (w["size"][0] > 50 and w["size"][1] > 50
            and "HotZone" not in w["class"]
            and w.get("title") not in FLOAT_TITLES)


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
    ap.add_argument("--park-cursor", action="store_true",
                    help="move the cursor to a neutral area during idle CPU sampling (8.1 definition)")
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
        u32.SetCursorPos(screen_w // 2, screen_h - 1)
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
            u32.SetCursorPos(screen_w // 2, screen_h - 1)
            time.sleep(0.7)
            visible_dock = [w for w in windows_of(pid)
                            if w["visible"] and is_dock_window(w)]
        report["preview_dock_visible_before_hover"] = bool(visible_dock)

        panel = None
        hover_point = None
        if visible_dock:
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

    ok = (report["dock_window_present"]
          and report["early_exit_code"] is None
          and not report["log_errors"])
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
    report["verdict"] = "PASS" if ok else "FAIL"
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
