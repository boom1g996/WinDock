"""WinDock M0 read-only capability probe.

Runs against the live Windows session and prints a JSON report. It NEVER moves,
hides, or modifies any window. Kept as a repeatable M0 acceptance artifact so
every P/Invoke claim in the feasibility report has a measured basis.

Usage: python tools/probe_capabilities.py
"""
from __future__ import annotations

import ctypes
import json
import platform
import subprocess
import winreg
from ctypes import wintypes

u32 = ctypes.WinDLL("user32", use_last_error=True)
dwm = ctypes.WinDLL("dwmapi", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)
shcore = ctypes.WinDLL("shcore", use_last_error=True)
k32 = ctypes.WinDLL("kernel32", use_last_error=True)

HMONITOR = getattr(wintypes, "HMONITOR", wintypes.HANDLE)
HMODULE = getattr(wintypes, "HMODULE", wintypes.HANDLE)


class RECT(ctypes.Structure):
    _fields_ = [
        ("left", ctypes.c_long), ("top", ctypes.c_long),
        ("right", ctypes.c_long), ("bottom", ctypes.c_long),
    ]


class APPBARDATA(ctypes.Structure):
    # Only the head is needed; SHAppBarMessage(ABM_GETSTATE) reads cbSize/hWnd.
    _fields_ = [
        ("cbSize", wintypes.DWORD), ("hWnd", wintypes.HWND),
        ("uCallbackMessage", wintypes.UINT), ("uEdge", wintypes.UINT),
        ("rc", RECT), ("lParam", wintypes.LPARAM),
    ]


WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
MONITORENUMPROC = ctypes.WINFUNCTYPE(
    wintypes.BOOL, HMONITOR, wintypes.HDC, ctypes.POINTER(RECT), wintypes.LPARAM
)

u32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
u32.FindWindowW.restype = wintypes.HWND
u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(RECT)]
u32.GetWindowRect.restype = wintypes.BOOL
u32.IsWindowVisible.argtypes = [wintypes.HWND]
u32.IsWindowVisible.restype = wintypes.BOOL
u32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_int32
u32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
u32.GetClassNameW.restype = ctypes.c_int
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
u32.GetWindowThreadProcessId.restype = wintypes.DWORD
u32.GetSystemMetrics.argtypes = [ctypes.c_int]
u32.GetSystemMetrics.restype = ctypes.c_int
u32.EnumWindows.argtypes = [WNDENUMPROC, wintypes.LPARAM]
u32.EnumWindows.restype = wintypes.BOOL
u32.EnumDisplayMonitors.argtypes = [wintypes.HDC, ctypes.c_void_p, MONITORENUMPROC, wintypes.LPARAM]
u32.EnumDisplayMonitors.restype = wintypes.BOOL
u32.GetForegroundWindow.restype = wintypes.HWND

dwm.DwmIsCompositionEnabled.argtypes = [ctypes.POINTER(wintypes.BOOL)]
dwm.DwmIsCompositionEnabled.restype = ctypes.c_long
dwm.DwmGetWindowAttribute.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]
dwm.DwmGetWindowAttribute.restype = ctypes.c_long

shell32.SHQueryUserNotificationState.argtypes = [ctypes.POINTER(ctypes.c_int)]
shell32.SHQueryUserNotificationState.restype = ctypes.c_long
shell32.IsUserAnAdmin.restype = wintypes.BOOL
shell32.SHAppBarMessage.argtypes = [wintypes.DWORD, ctypes.POINTER(APPBARDATA)]
shell32.SHAppBarMessage.restype = ctypes.c_size_t

shcore.GetDpiForMonitor.argtypes = [HMONITOR, ctypes.c_int, ctypes.POINTER(wintypes.UINT), ctypes.POINTER(wintypes.UINT)]
shcore.GetDpiForMonitor.restype = ctypes.c_long

k32.LoadLibraryW.argtypes = [wintypes.LPCWSTR]
k32.LoadLibraryW.restype = HMODULE
k32.GetProcAddress.argtypes = [HMODULE, wintypes.LPCSTR]
k32.GetProcAddress.restype = ctypes.c_void_p

GWL_EXSTYLE = -20
WS_EX_TOOLWINDOW = 0x00000080
DWMWA_CLOAKED = 14
DWMWA_EXTENDED_FRAME_BOUNDS = 9

QUNS_NAMES = {
    1: "QUNS_NOT_PRESENT", 2: "QUNS_BUSY", 3: "QUNS_RUNNING_D3D_FULL_SCREEN",
    4: "QUNS_PRESENTATION_MODE", 5: "QUNS_ACCEPTS_NOTIFICATIONS",
    6: "QUNS_QUIET_TIME", 7: "QUNS_APP",
}

API_EXPORTS = {
    "user32.dll": [
        "SetWindowPos", "SetWindowsHookExW", "UnhookWindowsHookEx",
        "RegisterWindowMessageW", "GetForegroundWindow", "SetForegroundWindow",
        "AttachThreadInput", "GetDpiForWindow", "SetWindowLongW",
        "GetWindowRect", "FlashWindowEx", "TrackPopupMenuEx",
        "SetWinEventHook", "SendMessageTimeoutW", "GetWindowThreadProcessId",
        "GetDpiForSystem", "SetWindowCompositionAttribute", "PrintWindow",
        "IsIconic", "ShowWindow", "GetWindowDisplayAffinity",
    ],
    "dwmapi.dll": [
        "DwmRegisterThumbnail", "DwmUpdateThumbnailProperties",
        "DwmUnregisterThumbnail", "DwmQueryThumbnailSourceSize",
        "DwmSetWindowAttribute", "DwmGetWindowAttribute",
        "DwmIsCompositionEnabled", "DwmEnableBlurBehindWindow",
    ],
    "shell32.dll": [
        "SHAppBarMessage", "SHQueryUserNotificationState", "ShellExecuteExW",
        "SHGetFileInfoW", "SHCreateItemFromParsingName",
    ],
    "shcore.dll": ["GetDpiForMonitor", "SetProcessDpiAwareness"],
    "ole32.dll": ["CoCreateInstance", "CoInitializeEx"],
}


def api_exports() -> dict:
    result = {}
    for dll, names in API_EXPORTS.items():
        handle = k32.LoadLibraryW(dll)
        result[dll] = {
            name: bool(k32.GetProcAddress(handle, name.encode("ascii")))
            for name in names
        }
    return result


def enumerate_windows() -> dict:
    classes: dict[str, int] = {}
    trays: list[dict] = []
    total = visible = cloaked = tool = 0

    def callback(hwnd, _lparam):
        nonlocal total, visible, cloaked, tool
        total += 1
        buf = ctypes.create_unicode_buffer(256)
        u32.GetClassNameW(hwnd, buf, 256)
        cls = buf.value
        if cls in ("Shell_TrayWnd", "Shell_SecondaryTrayWnd"):
            r = RECT()
            u32.GetWindowRect(hwnd, ctypes.byref(r))
            trays.append({
                "hwnd": hwnd, "class": cls,
                "rect": [r.left, r.top, r.right, r.bottom],
                "size": [r.right - r.left, r.bottom - r.top],
            })
        if not u32.IsWindowVisible(hwnd):
            return True
        visible += 1
        c = ctypes.c_int(0)
        hr = dwm.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, ctypes.byref(c), 4)
        if hr == 0 and c.value != 0:
            cloaked += 1
            return True
        if u32.GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW:
            tool += 1
            return True
        classes[cls] = classes.get(cls, 0) + 1
        return True

    u32.EnumWindows(WNDENUMPROC(callback), 0)
    return {
        "top_level_total": total,
        "visible": visible,
        "cloaked_filtered_out": cloaked,
        "toolwindow_filtered_out": tool,
        "enumerable_classes": dict(sorted(classes.items(), key=lambda kv: -kv[1])[:15]),
        "tray_windows": trays,
    }


def enumerate_monitors() -> list:
    found = []

    def callback(hmon, _hdc, rect, _lparam):
        dx, dy = wintypes.UINT(0), wintypes.UINT(0)
        hr = shcore.GetDpiForMonitor(hmon, 0, ctypes.byref(dx), ctypes.byref(dy))
        r = rect.contents
        found.append({
            "rect": [r.left, r.top, r.right, r.bottom],
            "size": [r.right - r.left, r.bottom - r.top],
            "effective_dpi": dx.value if hr == 0 else None,
            "dpi_hr": hex(hr & 0xFFFFFFFF),
        })
        return True

    u32.EnumDisplayMonitors(None, None, MONITORENUMPROC(callback), 0)
    return found


def running_processes() -> dict:
    out = {}
    try:
        text = subprocess.run(
            ["tasklist", "/FO", "CSV", "/NH"], capture_output=True, text=True,
            timeout=15, encoding="utf-8", errors="replace",
        ).stdout
        for line in text.splitlines():
            name = line.split('","')[0].strip('"').lower()
            for key in ("explorer.exe", "startallback", "explorerpatcher", "translucenttb", "windock", "rainmeter"):
                if key in name:
                    out[key] = out.get(key, 0) + 1
    except Exception as exc:  # probe must never fail the whole run
        out["error"] = repr(exc)
    return out


def theme_state() -> dict:
    state = {}
    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
        ) as key:
            state["apps_light_theme"], _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
    except OSError as exc:
        state["apps_light_theme_error"] = str(exc)
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\DWM") as key:
            state["colorization_color"], _ = winreg.QueryValueEx(key, "ColorizationColor")
    except OSError as exc:
        state["colorization_error"] = str(exc)
    return state


def main() -> None:
    tray = u32.FindWindowW("Shell_TrayWnd", None)
    rect = RECT()
    got = u32.GetWindowRect(tray, ctypes.byref(rect)) if tray else 0
    quns = ctypes.c_int(-1)
    qhr = shell32.SHQueryUserNotificationState(ctypes.byref(quns))
    comp = wintypes.BOOL(0)
    chr_ = dwm.DwmIsCompositionEnabled(ctypes.byref(comp))
    abd = APPBARDATA()
    abd.cbSize = ctypes.sizeof(APPBARDATA)
    appbar_state = shell32.SHAppBarMessage(4, ctypes.byref(abd))  # ABM_GETSTATE

    # DWMWA_SYSTEMBACKDROP_TYPE = 38 (Win11 22H2+). Query only; never set it.
    backdrop = ctypes.c_int(-1)
    backdrop_hr = dwm.DwmGetWindowAttribute(tray, 38, ctypes.byref(backdrop), 4) if tray else -1

    monitors = enumerate_monitors()
    window_scan = enumerate_windows()
    trays = window_scan["tray_windows"]

    def infer_edge(t: dict) -> str:
        for m in monitors:
            mr = m["rect"]
            r = t["rect"]
            if r[0] == mr[0] and r[2] == mr[2] and (r[3] - r[1]) < (r[2] - r[0]):
                return "top" if r[1] == mr[1] else "bottom"
            if r[1] == mr[1] and r[3] == mr[3] and (r[2] - r[0]) < (r[3] - r[1]):
                return "left" if r[0] == mr[0] else "right"
        return "unknown"

    for t in trays:
        t["inferred_edge"] = infer_edge(t)

    report = {
        "host": {
            "platform": platform.platform(),
            "build": platform.version(),
            "architecture": platform.machine(),
            "python": platform.python_version(),
            "is_admin": bool(shell32.IsUserAnAdmin()),
            "remote_session": bool(u32.GetSystemMetrics(0x1000)),  # SM_REMOTESESSION
        },
        "dwm": {
            "composition_enabled": bool(comp.value),
            "hr": hex(chr_ & 0xFFFFFFFF),
            "build_number": int(platform.version().split(".")[-1]),
            "system_backdrop_supported_by_build": int(platform.version().split(".")[-1]) >= 22621,
            # Queried on the existing taskbar window: proves the attribute is wired up.
            "backdrop_attr_probe": {
                "value": backdrop.value,
                "hr": hex(backdrop_hr & 0xFFFFFFFF),
            },
        },
        "notification_state": {
            "value": quns.value,
            "name": QUNS_NAMES.get(quns.value, "UNKNOWN"),
            "hr": hex(qhr & 0xFFFFFFFF),
        },
        "native_taskbar": {
            "hwnd": tray or None,
            "rect": [rect.left, rect.top, rect.right, rect.bottom] if got else None,
            "size": [rect.right - rect.left, rect.bottom - rect.top] if got else None,
            "appbar_getstate": appbar_state,  # 0 = normal, 1 = autohide, 2 = always-on-top
            "all_tray_windows": trays,
        },
        "displays": {
            "count": u32.GetSystemMetrics(80),  # SM_CMONITORS
            "monitors": monitors,
        },
        "appearance": theme_state(),
        "windows": window_scan,
        "api_exports": api_exports(),
        "processes": running_processes(),
        "scope_note": "READ-ONLY probe. No window was moved, hidden, or modified.",
    }
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
