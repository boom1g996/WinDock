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
u32.GetSystemMetrics.argtypes = [ctypes.c_int]
u32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
u32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
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
        found.append({
            "class": name.value,
            "rect": [rect.left, rect.top, rect.right, rect.bottom],
            "size": [rect.right - rect.left, rect.bottom - rect.top],
            "visible": bool(u32.IsWindowVisible(hwnd)),
        })
        return True

    u32.EnumWindows(callback, 0)
    return found


def is_dock_window(w: dict) -> bool:
    """Dock 主窗口：宽高都 >50；排除 2px 热区窗口与其它工具窗口。"""
    return w["size"][0] > 50 and w["size"][1] > 50 and "HotZone" not in w["class"]


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
    args = ap.parse_args()
    sample_seconds = max(1.0, args.sample_seconds)
    result_path = Path(args.output) if args.output else RESULT
    result_path.parent.mkdir(parents=True, exist_ok=True)

    started_at = datetime.now()
    report: dict = {"timestamp": started_at.isoformat(timespec="seconds"), "exe": str(EXE)}
    if not EXE.is_file():
        report["error"] = "DockHost.exe not found; run build_m1.ps1 -Publish first"
        result_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(report["error"])
        return 2

    started_new = False
    pid = existing_pid()
    cpu0 = None
    if pid is None:
        process = subprocess.Popen([str(EXE)], cwd=str(EXE.parent))
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

    ok = (report["dock_window_present"]
          and report["early_exit_code"] is None
          and not report["log_errors"])
    if args.test_autohide:
        ok = ok and report["auto_hidden"] and report["autohide_restore"]
    report["verdict"] = "PASS" if ok else "FAIL"

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
