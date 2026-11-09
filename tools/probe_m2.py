#!/usr/bin/env python3
"""M2 能力预研：只读 + 可回滚写入，输出 docs/M2-probe-raw.json。

对应 docs/M2-设计说明与任务拆解.md §4 的 P0 前置：
  1. DWM 缩略图注册 / 尺寸查询 / 注销（FR-401~404）
  2. SHQueryUserNotificationState 全屏态 + 前台窗口 rect 判据（FR-603）
  3. FlashWindowEx 是否会触发 WinEvent（FR-203 注意跳动可行性）
  4. RegisterHotKey（Ctrl+Alt+D，FR-602）
  5. 2px 分层透明热区窗口（FR-601）
  6. 显示器绝对 rect（热区定位，M0 已知副屏负坐标）

约束：不修改系统状态、不注入其他进程、不提权；本进程创建的窗口 / 热键 / 缩略图 / 钩子
均在退出前逐个销毁。任何单项失败不影响其余项与 JSON 落盘。
"""
from __future__ import annotations

import ctypes
import json
import os
import sys
import time
from ctypes import wintypes
from datetime import datetime, timezone

user32 = ctypes.WinDLL("user32", use_last_error=True)
dwmapi = ctypes.WinDLL("dwmapi", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

LRESULT = ctypes.c_ssize_t
HRESULT = ctypes.c_long
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT,
                             wintypes.WPARAM, wintypes.LPARAM)
WINEVENTPROC = ctypes.WINFUNCTYPE(None, wintypes.HANDLE, wintypes.DWORD,
                                  wintypes.HWND, wintypes.LONG, wintypes.LONG,
                                  wintypes.DWORD, wintypes.DWORD)
MONITORENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HMONITOR,
                                     wintypes.HDC, ctypes.POINTER(wintypes.RECT),
                                     wintypes.LPARAM)


class WNDCLASSEXW(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.UINT), ("style", wintypes.UINT),
        ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int),
        ("cbWndExtra", ctypes.c_int), ("hInstance", wintypes.HINSTANCE),
        ("hIcon", wintypes.HICON), ("hCursor", wintypes.HANDLE),
        ("hbrBackground", wintypes.HBRUSH), ("lpszMenuName", wintypes.LPCWSTR),
        ("lpszClassName", wintypes.LPCWSTR), ("hIconSm", wintypes.HICON),
    ]


class MSG(ctypes.Structure):
    _fields_ = [
        ("hwnd", wintypes.HWND), ("message", wintypes.UINT),
        ("wParam", wintypes.WPARAM), ("lParam", wintypes.LPARAM),
        ("time", wintypes.DWORD), ("pt", wintypes.POINT),
    ]


class SIZE(ctypes.Structure):
    _fields_ = [("cx", wintypes.LONG), ("cy", wintypes.LONG)]


class FLASHWINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.UINT), ("hwnd", wintypes.HWND),
        ("dwFlags", wintypes.DWORD), ("uCount", wintypes.UINT),
        ("dwTimeout", wintypes.DWORD),
    ]


class MONITORINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT),
        ("rcWork", wintypes.RECT), ("dwFlags", wintypes.DWORD),
    ]


kernel32.GetProcAddress.argtypes = [wintypes.HMODULE, wintypes.LPCSTR]
kernel32.GetProcAddress.restype = wintypes.LPVOID
kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
kernel32.GetModuleHandleW.restype = wintypes.HMODULE
HINSTANCE = kernel32.GetModuleHandleW(None)

user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT,
                                  wintypes.WPARAM, wintypes.LPARAM]
user32.DefWindowProcW.restype = LRESULT
user32.TranslateMessage.argtypes = [ctypes.POINTER(MSG)]
user32.DispatchMessageW.argtypes = [ctypes.POINTER(MSG)]
user32.DispatchMessageW.restype = LRESULT
user32.RegisterClassExW.argtypes = [ctypes.POINTER(WNDCLASSEXW)]
user32.RegisterClassExW.restype = wintypes.ATOM
user32.CreateWindowExW.argtypes = [wintypes.DWORD, wintypes.LPCWSTR,
                                   wintypes.LPCWSTR, wintypes.DWORD,
                                   ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                   ctypes.c_int, wintypes.HWND, wintypes.HMENU,
                                   wintypes.HINSTANCE, wintypes.LPVOID]
user32.CreateWindowExW.restype = wintypes.HWND
user32.DestroyWindow.argtypes = [wintypes.HWND]
user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
user32.GetWindowLongW.restype = wintypes.LONG
user32.SetLayeredWindowAttributes.argtypes = [wintypes.HWND, wintypes.COLORREF,
                                              wintypes.BYTE, wintypes.DWORD]
user32.PeekMessageW.argtypes = [ctypes.POINTER(MSG), wintypes.HWND,
                                wintypes.UINT, wintypes.UINT, wintypes.UINT]
user32.SetWinEventHook.argtypes = [wintypes.DWORD, wintypes.DWORD,
                                   wintypes.HMODULE, WINEVENTPROC,
                                   wintypes.DWORD, wintypes.DWORD, wintypes.DWORD]
user32.SetWinEventHook.restype = wintypes.HANDLE
user32.UnhookWinEvent.argtypes = [wintypes.HANDLE]
user32.RegisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int,
                                  wintypes.UINT, wintypes.UINT]
user32.UnregisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int]
user32.FlashWindowEx.argtypes = [ctypes.POINTER(FLASHWINFO)]
user32.FlashWindowEx.restype = wintypes.BOOL
user32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
user32.FindWindowW.restype = wintypes.HWND
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetDesktopWindow.restype = wintypes.HWND
user32.GetSystemMetrics.argtypes = [ctypes.c_int]
user32.GetSystemMetrics.restype = ctypes.c_int
user32.EnumDisplayMonitors.argtypes = [wintypes.HDC, ctypes.POINTER(wintypes.RECT),
                                       MONITORENUMPROC, wintypes.LPARAM]
user32.GetMonitorInfoW.argtypes = [wintypes.HMONITOR, ctypes.POINTER(MONITORINFO)]
shell32.SHQueryUserNotificationState.argtypes = [ctypes.POINTER(wintypes.INT)]
shell32.SHQueryUserNotificationState.restype = HRESULT
dwmapi.DwmRegisterThumbnail.argtypes = [wintypes.HWND, wintypes.HWND,
                                        ctypes.POINTER(wintypes.HANDLE)]
dwmapi.DwmRegisterThumbnail.restype = HRESULT
dwmapi.DwmQueryThumbnailSourceSize.argtypes = [wintypes.HANDLE,
                                               ctypes.POINTER(SIZE)]
dwmapi.DwmQueryThumbnailSourceSize.restype = HRESULT
dwmapi.DwmUnregisterThumbnail.argtypes = [wintypes.HANDLE]
dwmapi.DwmUnregisterThumbnail.restype = HRESULT

CLASS_NAME = "WinDockM2Probe"
WND_PROC_REF = WNDPROC(lambda h, m, w, l: user32.DefWindowProcW(h, m, w, l))
EVENTS: list[dict] = []


def hr_name(hr: int) -> str:
    return "S_OK" if hr == 0 else f"HRESULT 0x{hr & 0xFFFFFFFF:08X}"


def make_window(class_name: str, ex_style: int, style: int,
                x: int, y: int, w: int, h: int) -> int:
    hwnd = user32.CreateWindowExW(ex_style, class_name, "WinDock M2 probe",
                                  style, x, y, w, h, 0, 0, HINSTANCE, None)
    if not hwnd:
        raise ctypes.WinError(ctypes.get_last_error())
    return hwnd


def rect_of(hwnd: int) -> list[int]:
    r = wintypes.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(r)):
        raise ctypes.WinError(ctypes.get_last_error())
    return [r.left, r.top, r.right, r.bottom]


def exports() -> dict:
    targets = {
        "dwmapi.dll": ["DwmRegisterThumbnail", "DwmUnregisterThumbnail",
                       "DwmUpdateThumbnailProperties", "DwmQueryThumbnailSourceSize",
                       "DwmIsCompositionEnabled"],
        "user32.dll": ["RegisterHotKey", "UnregisterHotKey", "FlashWindowEx",
                       "SetWinEventHook", "SetLayeredWindowAttributes",
                       "SetWindowCompositionAttribute", "GetMonitorInfoW",
                       "EnumDisplayMonitors", "GetForegroundWindow"],
        "shell32.dll": ["SHQueryUserNotificationState", "Shell_NotifyIconW",
                        "SHGetFileInfoW"],
    }
    libs = {"dwmapi.dll": dwmapi, "user32.dll": user32, "shell32.dll": shell32}
    out: dict = {}
    for lib, names in targets.items():
        found = {}
        for name in names:
            addr = kernel32.GetProcAddress(libs[lib]._handle, name.encode("ascii"))
            found[name] = bool(addr)
        out[lib] = found
    out["all_present"] = all(all(v.values()) for v in
                             (out[k] for k in targets))
    return out


def quns() -> dict:
    state = wintypes.INT(-1)
    hr = shell32.SHQueryUserNotificationState(ctypes.byref(state))
    names = {
        1: "QUNS_NOT_PRESENT", 2: "QUNS_BUSY",
        3: "QUNS_RUNNING_D3D_FULL_SCREEN", 4: "QUNS_PRESENTATION_MODE",
        5: "QUNS_ACCEPTS_NOTIFICATIONS", 6: "QUNS_QUIET_TIME", 7: "QUNS_APP",
    }
    return {"ok": hr == 0, "hr": hr_name(hr), "value": state.value,
            "name": names.get(state.value, "UNKNOWN"),
            "full_screen_flags": [3, 4]}


def foreground_geometry() -> dict:
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return {"ok": False, "reason": "no foreground window"}
    r = rect_of(hwnd)
    sw = user32.GetSystemMetrics(0)
    sh = user32.GetSystemMetrics(1)
    covers = r[0] <= 0 and r[1] <= 0 and r[2] >= sw and r[3] >= sh
    return {"ok": True, "hwnd": int(hwnd), "rect": r,
            "primary_screen": [sw, sh], "covers_primary": covers}


def monitors() -> dict:
    found: list[dict] = []

    def cb(hmon, _hdc, _lprc, _data):
        info = MONITORINFO()
        info.cbSize = ctypes.sizeof(MONITORINFO)
        if user32.GetMonitorInfoW(hmon, ctypes.byref(info)):
            m, w = info.rcMonitor, info.rcWork
            found.append({
                "monitor": [m.left, m.top, m.right, m.bottom],
                "work": [w.left, w.top, w.right, w.bottom],
                "primary": bool(info.dwFlags & 0x1),
            })
        return True

    ref = MONITORENUMPROC(cb)  # 保持引用直到调用结束
    user32.EnumDisplayMonitors(None, None, ref, 0)
    return {"ok": bool(found), "count": len(found), "items": found,
            "has_negative_coords": any(m["monitor"][0] < 0 or m["monitor"][1] < 0
                                       for m in found)}


def dwm_thumbnail(host: int) -> dict:
    out: dict = {}
    tray = user32.FindWindowW("Shell_TrayWnd", None)
    desktop = user32.GetDesktopWindow()
    for label, source in (("Shell_TrayWnd", tray), ("Desktop", desktop)):
        if not source:
            continue
        thumb = wintypes.HANDLE()
        hr = dwmapi.DwmRegisterThumbnail(host, source, ctypes.byref(thumb))
        entry = {"source": label, "source_hwnd": int(source),
                 "register": hr_name(hr), "register_ok": hr == 0}
        if hr == 0:
            size = SIZE()
            qhr = dwmapi.DwmQueryThumbnailSourceSize(thumb, ctypes.byref(size))
            entry["source_size"] = [size.cx, size.cy]
            entry["query_size"] = hr_name(qhr)
            uhr = dwmapi.DwmUnregisterThumbnail(thumb)
            entry["unregister"] = hr_name(uhr)
            entry["unregister_ok"] = uhr == 0
        out[label] = entry
        if hr == 0:
            break
    out["ok"] = any(v.get("register_ok") and v.get("unregister_ok")
                    for v in out.values() if isinstance(v, dict))
    return out


def hotkey() -> dict:
    MOD_CONTROL, MOD_ALT, MOD_NOREPEAT = 0x0002, 0x0001, 0x4000
    ok = bool(user32.RegisterHotKey(None, 0xBEEF,
                                    MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x44))
    err = ctypes.get_last_error() if not ok else 0
    unregistered = bool(user32.UnregisterHotKey(None, 0xBEEF)) if ok else None
    return {"ok": ok, "gesture": "Ctrl+Alt+D",
            "win32_error": err,
            "note": "失败通常表示已被其他进程占用" if not ok else "",
            "unregistered": unregistered}


def layered_hotzone() -> dict:
    WS_POPUP = 0x80000000
    WS_EX_LAYERED = 0x00080000
    WS_EX_TRANSPARENT = 0x00000020
    WS_EX_TOOLWINDOW = 0x00000080
    WS_EX_NOACTIVATE = 0x08000000
    GWL_EXSTYLE = -20
    LWA_ALPHA = 0x00000002
    sh = user32.GetSystemMetrics(1)
    hwnd = make_window(CLASS_NAME,
                       WS_EX_LAYERED | WS_EX_TRANSPARENT |
                       WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                       WS_POPUP, 100, sh - 2, 200, 2)
    try:
        set_ok = bool(user32.SetLayeredWindowAttributes(hwnd, 0, 1, LWA_ALPHA))
        rect = rect_of(hwnd)
        ex = user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        return {"ok": True, "set_layered_ok": set_ok, "rect": rect,
                "height_px": rect[3] - rect[1],
                "ex_style": f"0x{ex & 0xFFFFFFFF:08X}",
                "has_transparent": bool(ex & WS_EX_TRANSPARENT),
                "has_noactivate": bool(ex & WS_EX_NOACTIVATE),
                "has_toolwindow": bool(ex & WS_EX_TOOLWINDOW)}
    finally:
        user32.DestroyWindow(hwnd)


def flash_events(host: int) -> dict:
    EVENT_SYSTEM_ALERT = 0x0002
    EVENT_OBJECT_STATECHANGE = 0x800A
    EVENT_SYSTEM_FOREGROUND = 0x0003
    WINEVENT_OUTOFCONTEXT = 0x0000
    PM_REMOVE = 0x0001
    FLASHW_ALL = 0x0003
    FLASHW_TIMERNOFG = 0x000C

    EVENTS.clear()

    def cb(_hook, event, hwnd, _id_obj, _id_child, _thread, _time):
        EVENTS.append({"event": f"0x{event:08X}", "hwnd": int(hwnd or 0)})
        return None

    proc_ref = WINEVENTPROC(cb)
    hooks = []
    for lo, hi in ((EVENT_SYSTEM_ALERT, EVENT_SYSTEM_ALERT),
                   (EVENT_OBJECT_STATECHANGE, EVENT_OBJECT_STATECHANGE),
                   (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND)):
        h = user32.SetWinEventHook(lo, hi, None, proc_ref, 0, 0,
                                   WINEVENT_OUTOFCONTEXT)
        if h:
            hooks.append(h)
    flash = FLASHWINFO()
    flash.cbSize = ctypes.sizeof(FLASHWINFO)
    flash.hwnd = host
    flash.dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG
    flash.uCount = 2
    flash.dwTimeout = 120
    flashed = bool(user32.FlashWindowEx(ctypes.byref(flash)))
    deadline = time.monotonic() + 1.8
    msg = MSG()
    while time.monotonic() < deadline:
        while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
        time.sleep(0.02)
    for h in hooks:
        user32.UnhookWinEvent(h)
    alert = [e for e in EVENTS if e["event"] == "0x00000002"]
    statechange = [e for e in EVENTS if e["event"] == "0x0000800A"]
    return {"ok": True, "hooks_installed": len(hooks), "flash_triggered": flashed,
            "sample_seconds": 1.8, "event_count": len(EVENTS),
            "alert_count": len(alert), "statechange_count": len(statechange),
            "events_sample": EVENTS[:12],
            "verdict": ("reliable" if alert else "no-standard-winevent")}


def main() -> int:
    result: dict = {
        "timestamp": datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds"),
        "python": sys.version.split()[0],
        "os_build": getattr(sys.getwindowsversion(), "build", None),
        "scope": "M2 capability probe (read-only + reversible in-process writes)",
    }
    host = None
    try:
        wc = WNDCLASSEXW()
        wc.cbSize = ctypes.sizeof(WNDCLASSEXW)
        wc.lpfnWndProc = WND_PROC_REF
        wc.hInstance = kernel32.GetModuleHandleW(None)
        wc.lpszClassName = CLASS_NAME
        atom = user32.RegisterClassExW(ctypes.byref(wc))
        if not atom:
            raise ctypes.WinError(ctypes.get_last_error())
        host = make_window(CLASS_NAME, 0, 0x00000000, 0, 0, 320, 180)
    except Exception as exc:  # 窗口创建失败时其余只读项仍可跑
        result["probe_window"] = {"ok": False,
                                  "error": f"{type(exc).__name__}: {exc}"}

    tasks = {
        "exports": exports,
        "quns": quns,
        "foreground_geometry": foreground_geometry,
        "monitors": monitors,
        "hotkey": hotkey,
    }
    if host:
        tasks["dwm_thumbnail"] = lambda: dwm_thumbnail(host)
        tasks["layered_hotzone"] = layered_hotzone
        tasks["flash_events"] = lambda: flash_events(host)

    for name, fn in tasks.items():
        try:
            result[name] = fn()
        except Exception as exc:
            result[name] = {"ok": False,
                            "error": f"{type(exc).__name__}: {exc}"}
        print(f"[probe] {name}: ok={result[name].get('ok')}"
              + (f" err={result[name].get('error')}"
                 if result[name].get("error") else ""))

    if host:
        user32.DestroyWindow(host)
        result["probe_window"] = {"ok": True, "destroyed": True}

    output = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                          "docs", "M2-probe-raw.json")
    os.makedirs(os.path.dirname(output), exist_ok=True)
    with open(output, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=2)
    print(f"[probe] written: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
