"""WD-035 长稳实证采样器（零依赖）：按固定间隔采集 DockHost 的存活、CPU、
内存、句柄、窗口状态、日志错误与看门狗恢复事件，逐行写 JSONL，可随时中断续采。

用法：
    # 采 24 小时（默认 60s 一个点），Dock 不存在时自动拉起
    python tools/stability_m1.py --hours 24 --output docs/M2-stability.jsonl

    # 只看已有结果
    python tools/stability_m1.py --summary --output docs/M2-stability.jsonl

    # 只采 10 分钟做冒烟（验证采集链路本身）
    python tools/stability_m1.py --minutes 10 --interval 20 --output /tmp/stab.jsonl

判定口径与 tools/smoke_m1.py 一致：CPU% = CPU 累计秒增量 / 间隔秒数 × 100，
即「占单核百分比」，与需求 8.1 的空闲 CPU ≤0.3% 同口径。
"""
from __future__ import annotations

import argparse
import ctypes
import json
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import smoke_m1 as smoke  # noqa: E402  （复用 DPI 感知、CPU/内存/窗口枚举等工具）

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.GetProcessHandleCount.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32)]
k32.GetProcessHandleCount.restype = ctypes.c_bool

u32 = ctypes.WinDLL("user32", use_last_error=True)
u32.GetGuiResources.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
u32.GetGuiResources.restype = ctypes.c_uint32
GR_GDIOBJECTS = 0
GR_USEROBJECTS = 1


def gui_resources(pid: int) -> tuple[int | None, int | None]:
    """GDI / USER 对象数：句柄涨但这两者不涨 → 多为内核句柄（图标/文件/DWM），
    两者一起涨才是经典的 GDI/USER 泄漏。"""
    handle = smoke.k32.OpenProcess(0x1000, False, pid)
    if not handle:
        return None, None
    try:
        return (int(u32.GetGuiResources(handle, GR_GDIOBJECTS)),
                int(u32.GetGuiResources(handle, GR_USEROBJECTS)))
    finally:
        smoke.k32.CloseHandle(handle)


def handle_count(pid: int) -> int | None:
    handle = smoke.k32.OpenProcess(0x1000, False, pid)
    if not handle:
        return None
    try:
        value = ctypes.c_uint32(0)
        if not k32.GetProcessHandleCount(handle, ctypes.byref(value)):
            return None
        return int(value.value)
    finally:
        smoke.k32.CloseHandle(handle)


def private_bytes(pid: int) -> int | None:
    handle = smoke.k32.OpenProcess(0x1000, False, pid)
    if not handle:
        return None
    try:
        counters = smoke.PROCESS_MEMORY_COUNTERS()
        counters.cb = ctypes.sizeof(counters)
        if not smoke.psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
            return None
        return int(counters.PagefileUsage)
    finally:
        smoke.k32.CloseHandle(handle)


def ensure_host(exe: Path) -> int | None:
    pid = smoke.existing_pid()
    if pid is not None:
        return pid
    if not exe.is_file():
        print(f"[stability] DockHost not found: {exe}", file=sys.stderr)
        return None
    subprocess.Popen([str(exe)], cwd=str(exe.parent))
    for _ in range(40):
        time.sleep(0.25)
        pid = smoke.existing_pid()
        if pid is not None:
            return pid
    return None


def recovery_snapshot() -> tuple[float | None, dict | None]:
    path = smoke.RECOVERY
    if not path.is_file():
        return None, None
    return path.stat().st_mtime, smoke.read_recovery()


def error_lines(since: datetime) -> list[str]:
    out = []
    for line in smoke.read_log_tail(400):
        if "][ERROR]" not in line:
            continue
        stamp = smoke.parse_log_time(line)
        if stamp is not None and stamp >= since:
            out.append(line)
    return out


def summarize(path: Path, warmup_s: float = 300.0) -> int:
    if not path.is_file():
        print(f"[summary] no data file: {path}", file=sys.stderr)
        return 2

    samples = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            samples.append(json.loads(line))
        except ValueError:
            continue
    if not samples:
        print("[summary] empty data file", file=sys.stderr)
        return 2

    # 冷启动（JIT / 图标提取 / 首次 DWM 注册）不属于长稳漂移，先剔除预热窗口再算趋势。
    steady = [s for s in samples if s.get("uptime_s", 0) >= warmup_s] or samples
    cpus = [s["cpu_percent"] for s in steady if s.get("cpu_percent") is not None]
    mems = [s["working_set_mb"] for s in steady if s.get("working_set_mb") is not None]
    privs = [s["private_mb"] for s in steady if s.get("private_mb") is not None]
    handles = [s["handles"] for s in steady if s.get("handles") is not None]
    gdis = [s["gdi"] for s in steady if s.get("gdi") is not None]
    users = [s["user"] for s in steady if s.get("user") is not None]
    first, last = samples[0], samples[-1]
    span_h = (last["uptime_s"] - first["uptime_s"]) / 3600.0
    crashes = sum(1 for s in samples if s.get("host_alive") is False)
    recoveries = sum(1 for s in samples if s.get("new_recovery"))
    errors = sum(len(s.get("new_errors") or []) for s in samples)
    dock_missing = sum(1 for s in samples if s.get("dock_windows") == 0)

    def stat(values: list[float]) -> dict:
        if not values:
            return {}
        return {"min": round(min(values), 3), "avg": round(sum(values) / len(values), 3),
                "max": round(max(values), 3)}

    # 工作集会被 Windows 主动 trim（实测出现 32MB 的瞬时值），单看它会把「系统回收」
    # 误读成「内存下降」；私有字节更能反映真实占用趋势，两者都报。
    trims = 0
    if mems:
        ordered = sorted(mems)
        median = ordered[len(ordered) // 2]
        trims = sum(1 for m in mems if m < median * 0.5)

    summary = {
        "file": str(path),
        "samples": len(samples),
        "steady_samples": len(steady),
        "warmup_s": warmup_s,
        "window_hours": round(span_h, 2),
        "started": first.get("at"),
        "ended": last.get("at"),
        "cpu_percent": stat(cpus),
        "working_set_mb": stat(mems),
        "private_mb": stat(privs),
        "handles": stat([float(h) for h in handles]),
        "gdi": stat([float(g) for g in gdis]),
        "user": stat([float(u) for u in users]),
        "working_set_drift_mb": round((mems[-1] - mems[0]), 2) if len(mems) >= 2 else None,
        "private_drift_mb": round((privs[-1] - privs[0]), 2) if len(privs) >= 2 else None,
        "working_set_trim_samples": trims,
        "handle_drift": (handles[-1] - handles[0]) if len(handles) >= 2 else None,
        "gdi_drift": (gdis[-1] - gdis[0]) if len(gdis) >= 2 else None,
        "user_drift": (users[-1] - users[0]) if len(users) >= 2 else None,
        "host_dead_samples": crashes,
        "watchdog_recoveries": recoveries,
        "log_errors": errors,
        "samples_without_dock_window": dock_missing,
    }
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    path.with_suffix(".summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--hours", type=float, default=0.0, help="采样多少小时（与 --minutes 取较大者）")
    ap.add_argument("--minutes", type=float, default=60.0, help="采样多少分钟（默认 60）")
    ap.add_argument("--interval", type=float, default=60.0, help="采样间隔秒（默认 60）")
    ap.add_argument("--output", default="docs/M2-stability.jsonl")
    ap.add_argument("--exe", default=str(smoke.EXE))
    ap.add_argument("--summary", action="store_true", help="只汇总已有数据文件")
    ap.add_argument("--warmup", type=float, default=300.0,
                    help="汇总时剔除的预热秒数（冷启动不计入长稳漂移，默认 300）")
    ap.add_argument("--no-start", action="store_true", help="不自动拉起 DockHost，只采样已运行的实例")
    args = ap.parse_args()

    out = Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    if args.summary:
        return summarize(out, args.warmup)

    total_s = max(args.hours * 3600.0, args.minutes * 60.0)
    interval = max(5.0, args.interval)
    exe = Path(args.exe)

    pid = smoke.existing_pid() if args.no_start else ensure_host(exe)
    if pid is None:
        print("[stability] host not running and not started", file=sys.stderr)
        return 1

    started = datetime.now()
    last_recovery_mtime, last_recovery = recovery_snapshot()
    restarts = 0
    sample_no = 0
    print(f"[stability] start pid={pid} interval={interval}s total={total_s / 3600:.2f}h out={out}")
    out.open("a", encoding="utf-8").close()

    with out.open("a", encoding="utf-8") as fh:
        while True:
            cpu1 = smoke.cpu_time(pid) if pid else None
            time.sleep(interval)
            sample_no += 1

            alive = pid is not None and smoke.cpu_time(pid) is not None
            cpu2 = smoke.cpu_time(pid) if alive else None
            now = datetime.now()
            sample: dict = {
                "at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
                "local": now.isoformat(timespec="seconds"),
                "uptime_s": round(time.time() - started.timestamp(), 1),
                "pid": pid,
                "host_alive": alive,
                "cpu_percent": (round(max(0.0, (cpu2 - cpu1) / interval * 100.0), 3)
                                if cpu1 is not None and cpu2 is not None else None),
                "working_set_mb": None,
                "private_mb": None,
                "handles": None,
                "gdi": None,
                "user": None,
                "dock_windows": 0,
                "hotzone": False,
                "watchdog_pids": smoke.process_pids("WinDock.Watchdog.exe"),
                "new_errors": [],
                "new_recovery": None,
                "restarts": restarts,
            }

            if alive:
                ws = smoke.working_set(pid)
                priv = private_bytes(pid)
                sample["working_set_mb"] = round(ws / 1024 / 1024, 2) if ws else None
                sample["private_mb"] = round(priv / 1024 / 1024, 2) if priv else None
                sample["handles"] = handle_count(pid)
                sample["gdi"], sample["user"] = gui_resources(pid)
                windows = smoke.windows_of(pid)
                sample["dock_windows"] = len([w for w in windows if smoke.is_dock_window(w)])
                sample["hotzone"] = any("HotZone" in w["class"] for w in windows)

            sample["new_errors"] = error_lines(started)

            mtime, event = recovery_snapshot()
            if mtime is not None and mtime != last_recovery_mtime:
                sample["new_recovery"] = event
                last_recovery_mtime, last_recovery = mtime, event

            fh.write(json.dumps(sample, ensure_ascii=False) + "\n")
            fh.flush()

            flag = "" if alive else "  <== HOST DOWN"
            print(f"[stability] #{sample_no} {sample['local'][11:]} cpu={sample['cpu_percent']}% "
                  f"ws={sample['working_set_mb']}MB h={sample['handles']} "
                  f"dock={sample['dock_windows']} err={len(sample['new_errors'])}{flag}",
                  flush=True)

            if not alive:
                # 崩溃即记录并停止：长稳实证要的是「不崩」的证据，不做自动重启掩盖。
                print("[stability] host is down; stop sampling (evidence preserved)", flush=True)
                break

            if time.time() - started.timestamp() >= total_s:
                break
            pid = smoke.existing_pid()
            if pid is None:
                restarts += 1
                pid = ensure_host(exe)

    return summarize(out, args.warmup)


if __name__ == "__main__":
    raise SystemExit(main())
