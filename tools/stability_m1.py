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


def preheat(pid: int, rounds: int = 3) -> dict:
    """预热：先把交互路径各走几遍，让一次性成本落地，再取 24h 基线。

    K-21 实测：宿主冷启动后，每出现一种新的交互路径就会抬升一截私有字节，
    累计从 129.7MB 涨到 216.3MB（42 分钟），但**同类交互走顺后净增长为 0**
    （8 轮 3s 驻留实验 -2.68MB）。若不预热就开跑，这段台阶会被误读成「漂移」，
    24h 结论必然失真。
    """
    before = private_bytes(pid)
    script = Path(__file__).resolve().parent / "leak_ab_probe.py"
    cmd = [sys.executable, "-X", "utf8", str(script),
           "--cycles", str(rounds), "--phases", "A,B,D"]
    print(f"[stability] preheat: {' '.join(cmd)}", flush=True)
    try:
        subprocess.run(cmd, cwd=str(script.parent.parent), capture_output=True,
                       text=True, encoding="utf-8", errors="replace", timeout=1200)
    except (OSError, subprocess.SubprocessError) as ex:
        print(f"[stability] preheat skipped: {ex}", flush=True)
    time.sleep(20)  # 等预热后的 GC 落定，再量水位
    after = private_bytes(pid)
    info = {
        "before_mb": round(before / 1048576, 2) if before else None,
        "settled_mb": round(after / 1048576, 2) if after else None,
    }
    print(f"[stability] preheat done: {info}", flush=True)
    return info


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


def _stamp(sample: dict) -> datetime | None:
    try:
        return datetime.fromisoformat(sample["at"])
    except (KeyError, TypeError, ValueError):
        return None


def _span_hours(first: dict, last: dict) -> float:
    """按墙钟时间算跨度。不用 uptime_s：它相对「采集器启动」而非「宿主启动」，
    重跑一次采集就会清零，跨 run 相加会得出错误的总时长。"""
    a, b = _stamp(first), _stamp(last)
    if a is None or b is None:
        return 0.0
    return (b - a).total_seconds() / 3600.0


def _median_interval(samples: list[dict]) -> float:
    deltas = []
    previous = None
    for sample in samples:
        stamp = _stamp(sample)
        if stamp is None:
            continue
        if previous is not None:
            deltas.append((stamp - previous).total_seconds())
        previous = stamp
    if not deltas:
        return 60.0
    deltas.sort()
    return max(1.0, deltas[len(deltas) // 2])


def _steady_samples(samples: list[dict], warmup_s: float) -> list[dict]:
    """按宿主实例（pid）各自跳过前 warmup_s 秒：宿主重启后又是一次冷启动，
    不能让新实例的 JIT 计入上一次实例的稳态趋势。"""
    out = []
    anchors: dict = {}
    for sample in samples:
        pid = sample.get("pid")
        stamp = _stamp(sample)
        if stamp is None:
            out.append(sample)
            continue
        anchors.setdefault(pid, stamp)
        if (stamp - anchors[pid]).total_seconds() >= warmup_s:
            out.append(sample)
    return out


def _rolling(values: list[float | None], window: int) -> list[float]:
    """滑动窗口均值，窗口内 None 跳过；不足窗口长度的尾巴也要算（不足 5 分钟时）。"""
    cleaned = [v for v in values if v is not None]
    if not cleaned:
        return []
    window = max(1, min(window, len(cleaned)))
    out = []
    for i in range(len(cleaned) - window + 1):
        chunk = cleaned[i:i + window]
        out.append(round(sum(chunk) / len(chunk), 3))
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
    steady = _steady_samples(samples, warmup_s) or samples
    # 交互样本单独剔除：一次光标划过 Dock 就是 +5~30MB 台阶（K-21），
    # 混进来会让「24h 漂移」结论失真。数量仍如实上报。
    spikes = sum(1 for s in samples if s.get("suspected_interaction"))
    quiet = [s for s in steady if not s.get("suspected_interaction")] or steady
    cpus = [s["cpu_percent"] for s in quiet if s.get("cpu_percent") is not None]
    mems = [s["working_set_mb"] for s in quiet if s.get("working_set_mb") is not None]
    privs = [s["private_mb"] for s in quiet if s.get("private_mb") is not None]
    handles = [s["handles"] for s in quiet if s.get("handles") is not None]
    gdis = [s["gdi"] for s in quiet if s.get("gdi") is not None]
    users = [s["user"] for s in quiet if s.get("user") is not None]
    first, last = samples[0], samples[-1]
    span_h = _span_hours(first, last)
    crashes = sum(1 for s in samples if s.get("host_alive") is False)
    recoveries = sum(1 for s in samples if s.get("new_recovery"))
    errors = sum(len(s.get("new_errors") or []) for s in samples)
    dock_missing = sum(1 for s in samples if s.get("dock_windows") == 0)

    def stat(values: list[float]) -> dict:
        if not values:
            return {}
        return {"min": round(min(values), 3), "avg": round(sum(values) / len(values), 3),
                "max": round(max(values), 3)}

    # 需求 8.1 / AC-20 的口径是「任务管理器 5 分钟平均」与「工作集 ≤120MB」，
    # 单点 60s 值既会被系统记账刻度放大（见 K-13），也会被 OS trim 拉低（见 K-14），
    # 因此按 5 分钟滚动窗口算均值，并给出「达标窗口占比」——这才是可复核的判定。
    interval_s = _median_interval(samples)
    win = max(1, int(round(300.0 / interval_s)))
    cpu_5min = _rolling([s.get("cpu_percent") for s in steady], win)
    ws_5min = _rolling([s.get("working_set_mb") for s in steady], win)
    cpu_pass = [v for v in cpu_5min if v <= 0.3]
    ws_pass = [v for v in ws_5min if v <= 120.0]

    # 工作集会被 Windows 主动 trim（实测出现 32MB 的瞬时值），单看它会把「系统回收」
    # 误读成「内存下降」；私有字节更能反映真实占用趋势，两者都报。
    trims = 0
    if mems:
        ordered = sorted(mems)
        median = ordered[len(ordered) // 2]
        trims = sum(1 for m in mems if m < median * 0.5)

    runs = []
    for pid in dict.fromkeys(s.get("pid") for s in samples):
        group = [s for s in samples if s.get("pid") == pid]
        runs.append({"pid": pid, "samples": len(group),
                     "hours": round(_span_hours(group[0], group[-1]), 2),
                     "started": group[0].get("at"), "ended": group[-1].get("at")})

    summary = {
        "file": str(path),
        "samples": len(samples),
        "steady_samples": len(steady),
        "quiet_samples": len(quiet),
        "spike_samples": spikes,
        "warmup_s": warmup_s,
        "interval_s": interval_s,
        "window_hours": round(span_h, 2),
        "started": first.get("at"),
        "ended": last.get("at"),
        "runs": runs,
        "host_pid_changes": len(runs) - 1,
        "cpu_percent": stat(cpus),
        "cpu_5min": stat(cpu_5min),
        "cpu_5min_windows": len(cpu_5min),
        "cpu_5min_pass_ratio": round(len(cpu_pass) / len(cpu_5min), 3) if cpu_5min else None,
        "working_set_mb": stat(mems),
        "working_set_5min": stat(ws_5min),
        "working_set_pass_ratio": round(len(ws_pass) / len(ws_5min), 3) if ws_5min else None,
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
    ap.add_argument("--warmup", type=float, default=None,
                    help="汇总时剔除的预热秒数（默认：预热过 120，未预热 300）")
    ap.add_argument("--no-preheat", action="store_true",
                    help="跳过交互预热，直接从冷启动开始采样（默认会预热）")
    ap.add_argument("--no-start", action="store_true", help="不自动拉起 DockHost，只采样已运行的实例")
    args = ap.parse_args()

    out = Path(args.output)
    out.parent.mkdir(parents=True, exist_ok=True)
    if args.summary:
        return summarize(out, args.warmup if args.warmup is not None else 300.0)

    total_s = max(args.hours * 3600.0, args.minutes * 60.0)
    interval = max(5.0, args.interval)
    exe = Path(args.exe)

    pid = smoke.existing_pid() if args.no_start else ensure_host(exe)
    if pid is None:
        print("[stability] host not running and not started", file=sys.stderr)
        return 1

    started = datetime.now()
    preheat_info = None
    if not args.no_preheat:
        preheat_info = preheat(pid)
    warmup_s = args.warmup if args.warmup is not None else (120.0 if preheat_info else 300.0)

    last_recovery_mtime, last_recovery = recovery_snapshot()
    restarts = 0
    sample_no = 0
    print(f"[stability] start pid={pid} interval={interval}s total={total_s / 3600:.2f}h out={out}")
    out.open("a", encoding="utf-8").close()
    prev_priv: float | None = None

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
                "suspected_interaction": False,
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

            # 长稳期间任何交互（光标划过 Dock、打开预览、拖拽排序）都会带来 5~30MB 的
            # 私有字节台阶与 2~7% 的 CPU 尖峰（K-21 实测）。显式标记，汇总时与空闲漂移
            # 分开统计——否则一次误触就能把 24h 结论带偏。
            priv = sample["private_mb"]
            jump = (abs(priv - prev_priv)
                    if priv is not None and prev_priv is not None else 0.0)
            sample["suspected_interaction"] = bool(
                (sample["cpu_percent"] or 0.0) >= 2.0 or jump >= 5.0)
            if priv is not None:
                prev_priv = priv

            mtime, event = recovery_snapshot()
            if mtime is not None and mtime != last_recovery_mtime:
                sample["new_recovery"] = event
                last_recovery_mtime, last_recovery = mtime, event

            fh.write(json.dumps(sample, ensure_ascii=False) + "\n")
            fh.flush()

            flag = "" if alive else "  <== HOST DOWN"
            spike = "  <== 疑似交互" if sample["suspected_interaction"] else ""
            print(f"[stability] #{sample_no} {sample['local'][11:]} cpu={sample['cpu_percent']}% "
                  f"ws={sample['working_set_mb']}MB priv={sample['private_mb']}MB "
                  f"h={sample['handles']} gdi={sample['gdi']}/{sample['user']} "
                  f"dock={sample['dock_windows']} err={len(sample['new_errors'])}{flag}{spike}",
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

    return summarize(out, warmup_s)


if __name__ == "__main__":
    raise SystemExit(main())
