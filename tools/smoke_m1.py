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
    found: list[dict] = []

    @WNDENUMPROC
    def callback(hwnd, _lparam):
        if not u32.IsWindowVisible(hwnd):
            return True
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
        })
        return True

    u32.EnumWindows(callback, 0)
    return found


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
    args = ap.parse_args()
    sample_seconds = max(1.0, args.sample_seconds)

    started_at = datetime.now()
    report: dict = {"timestamp": started_at.isoformat(timespec="seconds"), "exe": str(EXE)}
    if not EXE.is_file():
        report["error"] = "DockHost.exe not found; run build_m1.ps1 -Publish first"
        RESULT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(report["error"])
        return 2

    started_new = False
    pid = existing_pid()
    if pid is None:
        process = subprocess.Popen([str(EXE)], cwd=str(EXE.parent))
        pid = process.pid
        started_new = True
        time.sleep(3)
        report["early_exit_code"] = process.poll()
    else:
        report["early_exit_code"] = None

    report["pid"] = pid
    report["started_new_process"] = started_new
    report["windows"] = windows_of(pid)
    report["visible_window_count"] = len(report["windows"])

    cpu1 = cpu_time(pid)
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

    report["verdict"] = (
        "PASS" if report["visible_window_count"] >= 1
        and report["early_exit_code"] is None
        else "FAIL"
    )

    RESULT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "log_tail"},
                     ensure_ascii=False, indent=2))
    print(f"Result written to {RESULT}")

    if args.kill:
        subprocess.run(["taskkill", "/PID", str(pid), "/F"],
                       capture_output=True, text=True, timeout=20)
        print(f"Terminated PID {pid}")
    return 0 if report["verdict"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
