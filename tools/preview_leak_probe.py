"""预览面板开关的泄漏探针（K-20 取证）。

背景：2026-11-10 10:32:39 长稳采样期间出现一次「1 个缩略图、存活 1.6s」的预览，
之后 GDI 22→28、私有字节 +30MB、`ui.animation_frame` 46→205，且**关闭后都没回落**。
这可能是首帧初始化的一次性开销，也可能是「每次悬停预览都涨一截」的真泄漏。
本探针用同样的路径重复 N 次，对比每轮前后的 GDI / USER / 句柄 / 私有字节。

用法（不重启宿主，直接测正在运行的实例）：
    python tools/preview_leak_probe.py --icon 钉钉 --cycles 4 --output docs/M2-preview-leak.json
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

NEUTRAL = (10, 200)  # 远离 Dock 与热区的中性点


def snapshot(pid: int) -> dict:
    ws = smoke.working_set(pid)
    priv = stab.private_bytes(pid)
    gdi, user = stab.gui_resources(pid)
    return {
        "working_set_mb": round(ws / 1024 / 1024, 2) if ws else None,
        "private_mb": round(priv / 1024 / 1024, 2) if priv else None,
        "handles": stab.handle_count(pid),
        "gdi": gdi,
        "user": user,
        "dock_windows": len([w for w in smoke.windows_of(pid) if smoke.is_dock_window(w)]),
    }


def diff(before: dict, after: dict) -> dict:
    out = {}
    for key, value in after.items():
        old = before.get(key)
        out[key] = value if old is None or value is None else round(value - old, 2)
    return out


def panel_open(pid: int) -> bool:
    return any(w.get("title") == "WinDock Preview" and w["visible"]
               for w in smoke.windows_of(pid))


def _press_toggle_hotkey() -> None:
    """产品自带 Ctrl+Alt+D 是 Toggle：Dock 隐藏时按下即滑入（实测有效）。
    热区悬停在自动化下不稳定（动画 180ms + 800ms 自动隐藏，时序很窄），
    所以唤不出时用热键兜底。"""
    user32 = smoke.u32
    keyup = 0x0002
    for vk in (0x11, 0x12, 0x44):          # Ctrl, Alt, D
        user32.keybd_event(vk, 0, 0, 0)
        time.sleep(0.04)
    for vk in (0x44, 0x12, 0x11):
        user32.keybd_event(vk, 0, keyup, 0)
        time.sleep(0.04)


def reveal_dock() -> list[dict]:
    """Dock 会自动隐藏滑出屏幕（探针实测图标中心 y=1661 > 屏高 1400，光标根本落不到）。
    先把光标压到底边 2px 热区把 Dock 唤出，再取布局；坐标仍在屏外就重试。"""
    screen_w = smoke.u32.GetSystemMetrics(0)
    screen_h = smoke.u32.GetSystemMetrics(1)
    for attempt in range(4):
        smoke.u32.SetCursorPos(screen_w // 2, screen_h - 1)
        time.sleep(0.7)
        layout = smoke.dump_layout()
        if layout and layout["icons"] and all(0 <= i["center"][1] <= screen_h
                                              for i in layout["icons"]):
            return layout["icons"]
        if attempt == 1:
            _press_toggle_hotkey()             # 热区没唤出来，用热键兜底
            time.sleep(0.6)
    return []


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--icon", default=None, help="悬停哪个图标（默认取第一个）")
    ap.add_argument("--cycles", type=int, default=4)
    ap.add_argument("--hover-seconds", type=float, default=2.0)
    ap.add_argument("--settle-seconds", type=float, default=2.5)
    ap.add_argument("--output", default=None)
    args = ap.parse_args()

    pid = smoke.existing_pid()
    if pid is None:
        print("[leak] DockHost 未运行；本探针不负责启动（避免污染长稳采样）", file=sys.stderr)
        return 1

    icons = reveal_dock()
    if not icons:
        print("[leak] 唤不出 Dock 或布局坐标仍在屏外", file=sys.stderr)
        return 2

    target = None
    if args.icon:
        target = next((i for i in icons if args.icon in i["name"]), None)
        if target is None:
            print(f"[leak] 未找到图标 {args.icon}，现有：{[i['name'] for i in icons]}",
                  file=sys.stderr)
            return 2
    target = target or icons[0]
    print(f"[leak] pid={pid} icon={target['name']} center={target['center']} cycles={args.cycles}")

    cycles = []
    smoke.u32.SetCursorPos(*NEUTRAL)
    time.sleep(args.settle_seconds)

    for i in range(1, args.cycles + 1):
        # 每轮重新唤出并取坐标：上一轮的邻域放大/自动隐藏会让旧坐标失效
        icons = reveal_dock() or icons
        if args.icon:
            target = next((x for x in icons if args.icon in x["name"]), target)
        else:
            target = next((x for x in icons if x["name"] == target["name"]), icons[0])
        cx, cy = target["center"]

        base = snapshot(pid)
        cpu0 = smoke.cpu_time(pid)

        smoke.u32.SetCursorPos(cx, cy)          # 悬停 -> 500ms 后弹面板
        opened = False
        for _ in range(8):
            time.sleep(0.25)
            if panel_open(pid):
                opened = True
                break
        time.sleep(max(0.0, args.hover_seconds - 2.0))
        during = snapshot(pid)

        smoke.u32.SetCursorPos(*NEUTRAL)        # 离开 -> 面板关闭
        time.sleep(args.settle_seconds)
        after = snapshot(pid)
        cpu1 = smoke.cpu_time(pid)

        cycle = {
            "cycle": i,
            "icon_center": [cx, cy],
            "panel_opened": opened,
            "panel_closed_on_leave": not panel_open(pid),
            "base": base,
            "during_hover": during,
            "after_close": after,
            "delta_hover": diff(base, during),
            "delta_after_close": diff(base, after),
            "cpu_seconds_hover": round((cpu1 - cpu0), 3),
        }
        cycles.append(cycle)
        print(f"[leak] #{i} 面板={'开' if opened else '未开'} "
              f"悬停耗 CPU {cycle['cpu_seconds_hover']}s | "
              f"开面板 Δgdi={cycle['delta_hover']['gdi']} Δpriv={cycle['delta_hover']['private_mb']}MB | "
              f"关后 Δgdi={cycle['delta_after_close']['gdi']} "
              f"Δpriv={cycle['delta_after_close']['private_mb']}MB "
              f"Δh={cycle['delta_after_close']['handles']} "
              f"关后已关={cycle['panel_closed_on_leave']}", flush=True)

    control = snapshot(pid)
    time.sleep(args.settle_seconds)
    control2 = snapshot(pid)

    gdi_after = [c["delta_after_close"]["gdi"] for c in cycles]
    priv_after = [c["delta_after_close"]["private_mb"] for c in cycles]
    result = {
        "pid": pid,
        "icon": target["name"],
        "cycles": cycles,
        "control_idle": {"before": control, "after": control2,
                         "delta": diff(control, control2)},
        "gdi_delta_per_cycle": gdi_after,
        "private_delta_per_cycle_mb": priv_after,
        "verdict": _verdict(cycles),
    }
    print(json.dumps(result["verdict"], ensure_ascii=False, indent=2))
    if args.output:
        Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2),
                                     encoding="utf-8")
        print(f"[leak] 结果已写入 {args.output}")
    return 0


def _verdict(cycles: list[dict]) -> dict:
    """判定看**跨轮漂移**，不看单轮增量。

    踩过的坑：面板关闭瞬间 GDI 常常 +2，但下一轮开始又回到原值（26↔28 振荡），
    若按「每轮增量 >0」判定会误报泄漏。真正的泄漏特征是**绝对值逐轮抬升**。
    """
    if not cycles:
        return {"kind": "no-data"}
    first_base = cycles[0]["base"]
    last_after = cycles[-1]["after_close"]
    gdi_drift = (last_after.get("gdi") or 0) - (first_base.get("gdi") or 0)
    priv_drift = (last_after.get("private_mb") or 0) - (first_base.get("private_mb") or 0)
    handle_drift = (last_after.get("handles") or 0) - (first_base.get("handles") or 0)
    gdi_series = [(c["after_close"].get("gdi") or 0) for c in cycles]
    priv_series = [round(c["after_close"].get("private_mb") or 0, 1) for c in cycles]

    if gdi_drift >= 4 or priv_drift >= 8 or handle_drift >= 20:
        kind = "per-cycle-leak"
        note = "绝对值逐轮抬升：每次悬停预览都留下资源，需按泄漏修"
    elif gdi_drift <= 2 and priv_drift <= 4:
        kind = "no-accumulation"
        note = ("关闭瞬间会有 +2 GDI 之类的瞬时抖动，但下一轮起点回到原值（振荡而非累积）；"
                f"{len(cycles)} 轮后 GDI 漂移 {gdi_drift}、私有字节漂移 {priv_drift:+.1f}MB")
    else:
        kind = "inconclusive"
        note = "漂移介于两者之间，需增加轮次或换更长的观察窗口"
    return {"kind": kind, "note": note,
            "gdi_drift": gdi_drift, "private_drift_mb": round(priv_drift, 2),
            "handle_drift": handle_drift,
            "gdi_after_close_series": gdi_series,
            "private_after_close_series_mb": priv_series}


if __name__ == "__main__":
    raise SystemExit(main())
