"""交互路径分段泄漏探针（K-21 取证）。

背景：2026-11-10 长稳采样期间，宿主私有字节从 129.7MB 阶梯式涨到 216.3MB
（42 分钟 +86MB，约 2MB/min），且**只在交互后跳变**、静置 3 分钟完全平稳。
跳变点都伴随 cpu-spike 4.5~6.6%，日志显示当时发生了「预览面板打开」与
`launcher.activate`。`preview_leak_probe.py` 只测「悬停开面板 → 离开」，
6 轮无累积（+2.2MB），说明涨的不是这条最简路径。

本探针把交互拆成三段，各自独立度量，定位是哪一段留下资源：

  A  悬停图标 → 面板打开 → 光标**不进面板** → 移开设法关闭
  B  悬停图标 → 面板打开 → 光标**移入面板**停留（触发 ToolTip）→ 移出 → 关闭
  C  悬停图标 → 面板打开 → **点击缩略图激活应用** → 移开

A 与 B 的差 = ToolTip / 面板内命中测试的开销；C 的增量 = 激活路径的开销。

用法（宿主须已在运行；本探针不负责启动）：
    python tools/leak_ab_probe.py --cycles 8 --output docs/M2-leak-ab.json
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import smoke_m1 as smoke  # noqa: E402
import stability_m1 as stab  # noqa: E402
from preview_leak_probe import (  # noqa: E402
    NEUTRAL, diff, panel_open, reveal_dock, snapshot,
)

PANEL_TITLE = "WinDock Preview"


def panel_rect(pid: int) -> list[int] | None:
    for w in smoke.windows_of(pid):
        if w.get("title") == PANEL_TITLE and w["visible"]:
            return w["rect"]
    return None


def hwnd_count(pid: int) -> int:
    """宿主全部顶层窗口数（含 ToolTip 的 popup 与 IME 框）。
    这是「窗口对象有没有被销毁」最直接的观察量。"""
    return len(smoke.windows_of(pid))


def deep_snapshot(pid: int) -> dict:
    snap = snapshot(pid)
    snap["hwnds"] = hwnd_count(pid)
    return snap


def wait_panel(pid: int, timeout: float = 2.5) -> tuple[bool, list[int] | None]:
    deadline = time.time() + timeout
    while time.time() < deadline:
        rect = panel_rect(pid)
        if rect:
            return True, rect
        time.sleep(0.2)
    return False, None


def park(seconds: float = 2.0) -> None:
    smoke.u32.SetCursorPos(*NEUTRAL)
    time.sleep(seconds)


def run_phase(pid: int, name: str, cycles: int, body, settle: float) -> dict:
    print(f"\n[ab] === 阶段 {name}：{cycles} 轮 ===", flush=True)
    park(settle)
    start = deep_snapshot(pid)
    rounds = []
    for i in range(1, cycles + 1):
        before = deep_snapshot(pid)
        detail = body(pid, i)
        park(settle)
        after = deep_snapshot(pid)
        rounds.append({
            "round": i,
            "detail": detail,
            "before": before,
            "after": after,
            "delta": diff(before, after),
        })
        d = rounds[-1]["delta"]
        print(f"[ab] {name}#{i} Δpriv={d['private_mb']:+.2f}MB "
              f"Δh={d['handles']:+d} Δgdi={d['gdi']:+d} Δuser={d['user']:+d} "
              f"Δhwnd={d['hwnds']:+d} | {detail}", flush=True)
    end = deep_snapshot(pid)
    total = {
        "private_mb": round((end["private_mb"] or 0) - (start["private_mb"] or 0), 2),
        "handles": (end["handles"] or 0) - (start["handles"] or 0),
        "gdi": (end["gdi"] or 0) - (start["gdi"] or 0),
        "user": (end["user"] or 0) - (start["user"] or 0),
        "hwnds": (end["hwnds"] or 0) - (start["hwnds"] or 0),
    }
    print(f"[ab] {name} 合计 {cycles} 轮：Δpriv={total['private_mb']:+.2f}MB "
          f"Δh={total['handles']:+d} Δgdi={total['gdi']:+d} Δhwnd={total['hwnds']:+d}",
          flush=True)
    return {"phase": name, "cycles": rounds, "total_delta": total,
            "start": start, "end": end}


def body_hover_only(pid: int, i: int) -> str:
    """A：只悬停图标，光标不进面板。"""
    icons = reveal_dock()
    if not icons:
        return "dock-unreachable"
    cx, cy = icons[0]["center"]
    smoke.u32.SetCursorPos(cx, cy)
    opened, _ = wait_panel(pid)
    time.sleep(1.0)
    return f"panel={'open' if opened else 'miss'}"


def body_enter_panel(pid: int, i: int) -> str:
    """B：悬停开面板后把光标移进面板中心，触发 ToolTip 与命中测试。"""
    icons = reveal_dock()
    if not icons:
        return "dock-unreachable"
    cx, cy = icons[0]["center"]
    smoke.u32.SetCursorPos(cx, cy)
    opened, rect = wait_panel(pid)
    if not opened or not rect:
        return "panel-miss"
    # 面板矩形是物理像素；光标移到面板下半部中心，避免落到标题条外
    px = (rect[0] + rect[2]) // 2
    py = rect[1] + int((rect[3] - rect[1]) * 0.7)
    smoke.u32.SetCursorPos(px, py)
    time.sleep(0.3)
    smoke.u32.SetCursorPos(px + 2, py)      # 轻微移动，确保 WPF 收到 mouse move
    time.sleep(0.9)
    return f"panel=open entered=({px},{py})"


def body_click_thumbnail(pid: int, i: int) -> str:
    """C：点击缩略图激活目标窗口（激活路径本身的开销）。"""
    icons = reveal_dock()
    if not icons:
        return "dock-unreachable"
    cx, cy = icons[0]["center"]
    smoke.u32.SetCursorPos(cx, cy)
    opened, rect = wait_panel(pid)
    if not opened or not rect:
        return "panel-miss"
    px = (rect[0] + rect[2]) // 2
    py = rect[1] + int((rect[3] - rect[1]) * 0.45)
    smoke.u32.SetCursorPos(px, py)
    time.sleep(0.25)
    smoke.u32.mouse_event(0x0002, 0, 0, 0, 0)          # LEFTDOWN
    time.sleep(0.05)
    smoke.u32.mouse_event(0x0004, 0, 0, 0, 0)          # LEFTUP
    time.sleep(0.8)
    return f"clicked=({px},{py})"


def body_enter_brief(pid: int, i: int) -> str:
    """D：进面板后**立即**退出，压缩驻留时长（对照 E，隔离「时长」这个变量）。"""
    return _enter_and_dwell(pid, 0.30)


def body_enter_long(pid: int, i: int) -> str:
    """E：进面板后停留 3 秒，其余动作与 D 完全一致。"""
    return _enter_and_dwell(pid, 3.00)


def _enter_and_dwell(pid: int, dwell: float) -> str:
    icons = reveal_dock()
    if not icons:
        return "dock-unreachable"
    cx, cy = icons[0]["center"]
    smoke.u32.SetCursorPos(cx, cy)
    opened, rect = wait_panel(pid)
    if not opened or not rect:
        return "panel-miss"
    px = (rect[0] + rect[2]) // 2
    py = rect[1] + int((rect[3] - rect[1]) * 0.7)
    smoke.u32.SetCursorPos(px, py)
    time.sleep(dwell)
    return f"panel=open dwell={dwell}s"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cycles", type=int, default=8)
    ap.add_argument("--settle-seconds", type=float, default=2.0)
    ap.add_argument("--phases", default="A,B,C", help="要跑的阶段，逗号分隔")
    ap.add_argument("--output", default=None)
    args = ap.parse_args()

    pid = smoke.existing_pid()
    if pid is None:
        print("[ab] DockHost 未运行；本探针不负责启动", file=sys.stderr)
        return 1

    baseline = deep_snapshot(pid)
    print(f"[ab] pid={pid} 基线 priv={baseline['private_mb']}MB "
          f"h={baseline['handles']} gdi={baseline['gdi']}/{baseline['user']} "
          f"hwnd={baseline['hwnds']}", flush=True)

    table = {"A": body_hover_only, "B": body_enter_panel, "C": body_click_thumbnail,
             "D": body_enter_brief, "E": body_enter_long}
    wanted = [p.strip().upper() for p in args.phases.split(",") if p.strip()]
    phases = []
    for key in wanted:
        if key not in table:
            print(f"[ab] 未知阶段 {key}", file=sys.stderr)
            return 2
        phases.append(run_phase(pid, key, args.cycles, table[key], args.settle_seconds))

    result = {
        "pid": pid,
        "baseline": baseline,
        "final": deep_snapshot(pid),
        "phases": phases,
        "verdict": _verdict(phases),
    }
    print("\n[ab] 判定：", json.dumps(result["verdict"], ensure_ascii=False, indent=2))
    if args.output:
        Path(args.output).write_text(
            json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"[ab] 结果已写入 {args.output}")
    return 0


def _verdict(phases: list[dict]) -> dict:
    """按阶段比「每轮平均私有字节增量」定位责任方。"""
    per_round = {}
    for ph in phases:
        n = len(ph["cycles"]) or 1
        per_round[ph["phase"]] = {
            "private_mb": round(ph["total_delta"]["private_mb"] / n, 2),
            "handles": round(ph["total_delta"]["handles"] / n, 1),
            "hwnds": round(ph["total_delta"]["hwnds"] / n, 2),
        }
    hot = [k for k, v in per_round.items() if v["private_mb"] >= 1.0]
    if not hot:
        note = ("三段均无显著累积（每轮 <1MB）：私有点的阶梯跳变需另找触发源，"
                "看跳变时刻的 Metrics 日志")
        kind = "no-phase-leak"
    else:
        note = f"以下阶段每轮私有字节增量 ≥1MB：{hot}；优先查该路径持有的窗口/位图"
        kind = "phase-leak-" + "-".join(hot)
    return {"kind": kind, "note": note, "per_round": per_round}


if __name__ == "__main__":
    raise SystemExit(main())
