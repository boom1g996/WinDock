using System.Runtime.InteropServices;
using WinDock.Core.Interfaces;
using static WinDock.Core.Native.NativeMethods;

namespace WinDock.Core.Services;

/// <summary>托盘右键菜单项：文本 + 动作（菜单每次弹出时重建，文本可动态刷新）。</summary>
public sealed record TrayMenuItem(string Text, Action Action)
{
    public bool IsSeparator { get; init; }
}

/// <summary>
/// 纯 Win32 托盘图标（WD-034 / K-01）：Shell_NotifyIconW + 原生消息窗口 +
/// CreatePopupMenu/TrackPopupMenu，替代 WinForms NotifyIcon（约 20MB 模块开销）。
///
/// 要点：
/// - 消息窗口复用 M2 热区的注册模式（RegisterClassExW + 静态委托保活），
///   但作为 message-only 窗口创建（HWND_MESSAGE 父级），不参与枚举与命中测试；
/// - 托盘消息（uCallbackMessage）在创建线程派发 → 必须在 UI 线程构造，
///   菜单动作天然回到 UI 线程，无需 Dispatcher 编组；
/// - 右键菜单每次弹出前由 provider 重建（toggle 文本随 Dock 状态刷新）；
/// - 气泡通知用 NIM_MODIFY | NIF_INFO，等价 WinForms ShowBalloonTip。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const string Module = "Tray";
    private const string ClassName = "WinDock.Tray.v1";
    private const uint CallbackMessage = 0x7E00; // WM_APP 家族内自选，避免与系统消息冲突
    private const uint TrayId = 1;

    private static readonly object StaticSync = new();
    private static readonly WndProc WndProcRef = WindowProc;
    private static readonly Dictionary<IntPtr, TrayIconService> Instances = new();
    private static ushort _classAtom;
    private static IntPtr _moduleHandle;

    private readonly ILogService _log;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menuProvider;
    private readonly Action? _doubleClick;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;
    private bool _disposed;

    public TrayIconService(ILogService log, Func<IReadOnlyList<TrayMenuItem>> menuProvider,
        Action? doubleClick = null)
    {
        _log = log;
        _menuProvider = menuProvider;
        _doubleClick = doubleClick;
        EnsureWindow();
    }

    public bool IsVisible => _added;

    /// <summary>显示托盘图标（幂等；未注册成功时静默降级）。</summary>
    public void Show(string tooltip)
    {
        if (_disposed || _hwnd == IntPtr.Zero || _added)
        {
            return;
        }

        var data = BaseData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.hIcon = _icon;
        data.szTip = Truncate(tooltip, 127);
        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            _log.Warn(Module, $"托盘图标添加失败 Win32Error={Marshal.GetLastWin32Error()}");
            return;
        }

        _added = true;
    }

    public void Hide()
    {
        if (!_added)
        {
            return;
        }

        var data = BaseData();
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>托盘气泡通知（等价 WinForms NotifyIcon.ShowBalloonTip）。</summary>
    public void ShowBalloon(string title, string text, bool warning)
    {
        if (_disposed || !_added)
        {
            return;
        }

        var data = BaseData();
        data.uFlags = NIF_INFO;
        data.szInfo = Truncate(text, 255);
        data.szInfoTitle = Truncate(title, 63);
        data.dwInfoFlags = warning ? NIIF_WARNING : 0;
        if (!Shell_NotifyIconW(NIM_MODIFY, ref data))
        {
            _log.Warn(Module, $"托盘气泡显示失败 Win32Error={Marshal.GetLastWin32Error()}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
        if (_hwnd != IntPtr.Zero)
        {
            lock (StaticSync)
            {
                Instances.Remove(_hwnd);
            }

            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private NOTIFYICONDATA BaseData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = TrayId,
        uCallbackMessage = CallbackMessage,
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            return;
        }

        lock (StaticSync)
        {
            if (_classAtom == 0)
            {
                _moduleHandle = GetModuleHandle(null!);
                var wndClass = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcRef),
                    hInstance = _moduleHandle,
                    lpszClassName = ClassName,
                };
                _classAtom = RegisterClassExW(ref wndClass);
                if (_classAtom == 0)
                {
                    _log.Warn(Module, $"注册托盘窗口类失败 Win32Error={Marshal.GetLastWin32Error()}，托盘降级为不可用");
                    return;
                }
            }
        }

        // message-only 窗口：不出现在 EnumWindows、不参与命中测试（HotZone 模式的变体）。
        _hwnd = CreateWindowExW(0, ClassName, "WinDock tray", 0,
            0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, _moduleHandle, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            _log.Warn(Module, $"创建托盘消息窗口失败 Win32Error={Marshal.GetLastWin32Error()}");
            return;
        }

        // IDI_APPLICATION 共享图标；序数资源必须以 MAKEINTRESOURCE（数值指针）传入。
        _icon = LoadImageW(IntPtr.Zero, new IntPtr(IDI_APPLICATION), IMAGE_ICON,
            0, 0, LR_SHARED | LR_DEFAULTSIZE);
        if (_icon == IntPtr.Zero)
        {
            _log.Warn(Module, $"加载托盘图标失败 Win32Error={Marshal.GetLastWin32Error()}");
        }

        lock (StaticSync)
        {
            Instances[_hwnd] = this;
        }
    }

    private void OnTrayMessage(uint message)
    {
        if (_disposed)
        {
            return;
        }

        switch (message)
        {
            case WM_RBUTTONUP:
                ShowContextMenu();
                break;
            case WM_LBUTTONDBLCLK:
                try
                {
                    _doubleClick?.Invoke();
                }
                catch (Exception ex)
                {
                    _log.Error(Module, "托盘双击动作失败", ex);
                }

                break;
        }
    }

    /// <summary>弹出原生右键菜单；TPM_RETURNCMD 同步返回选择项 id 后立即派发动作。</summary>
    private void ShowContextMenu()
    {
        IReadOnlyList<TrayMenuItem> items;
        try
        {
            items = _menuProvider();
        }
        catch (Exception ex)
        {
            _log.Error(Module, "构建托盘菜单失败", ex);
            return;
        }

        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            _log.Warn(Module, $"CreatePopupMenu 失败 Win32Error={Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            uint id = 1;
            var actions = new List<Action>();
            foreach (TrayMenuItem item in items)
            {
                if (item.IsSeparator)
                {
                    if (!AppendMenuW(menu, MF_SEPARATOR, 0, string.Empty))
                    {
                        _log.Warn(Module, "AppendMenu(分隔线) 失败");
                    }

                    continue;
                }

                if (AppendMenuW(menu, MF_STRING, id, Truncate(item.Text, 255)))
                {
                    actions.Add(item.Action);
                    id++;
                }
            }

            // 弹菜单前必须 SetForegroundWindow，否则点击菜单外区域不会收起（SDK 明确要求）。
            SetForegroundWindow(_hwnd);
            if (!GetCursorPos(out POINT point))
            {
                return;
            }

            int chosen = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN,
                point.X, point.Y, 0, _hwnd, IntPtr.Zero);            if (chosen >= 1 && chosen <= actions.Count)
            {
                try
                {
                    actions[chosen - 1]();
                }
                catch (Exception ex)
                {
                    _log.Error(Module, "托盘菜单动作失败", ex);
                }
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            TrayIconService? tray;
            lock (StaticSync)
            {
                Instances.TryGetValue(hwnd, out tray);
            }

            tray?.OnTrayMessage(LowWord(lParam));
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static uint LowWord(IntPtr value) => (uint)(value.ToInt64() & 0xFFFF);
}
