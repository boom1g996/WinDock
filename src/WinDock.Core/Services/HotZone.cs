using System.Runtime.InteropServices;
using WinDock.Core.Interfaces;
using static WinDock.Core.Native.NativeMethods;

namespace WinDock.Core.Services;

/// <summary>
/// 2px 原生分层热区窗口（WD-015 / D5 / FR-601）：位于 Dock 所在显示器的底边，
/// 只在鼠标进入时发出一次 Entered，不安装全局鼠标钩子、不轮询光标位置。
///
/// 实现要点：
/// - 用 Win32 原生窗口而非第二个 WPF Window，避免额外 D3D 渲染表面（K-01 内存）。
/// - **不加 WS_EX_TRANSPARENT**：该样式会让鼠标消息直接穿透到下层窗口，
///   热区就收不到 WM_MOUSEMOVE（M2 预研只验证了样式位可设置，未验证命中测试）。
/// - 分层窗口 alpha=1 保持几乎不可见，同时保留命中测试；NOACTIVATE 保证不抢焦点。
/// - WndProc 委托用静态字段保活，防止 GC 后回调地址失效。
/// </summary>
public sealed class HotZone : IHotZone
{
    private const string Module = "HotZone";
    private const string ClassName = "WinDock.HotZone.v1";
    private const uint ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW_FLAG;

    private static readonly object StaticSync = new();
    private static readonly WndProc WndProcRef = WindowProc;
    private static readonly Dictionary<IntPtr, HotZone> Instances = new();
    private static ushort _classAtom;
    private static IntPtr _moduleHandle;

    private readonly ILogService _log;
    private IntPtr _hwnd;
    private int _heightPx = 2;
    private DateTime _lastEnterUtc = DateTime.MinValue;
    private bool _enabled;
    private bool _visible;
    private bool _disposed;

    public HotZone(ILogService log)
    {
        _log = log;
    }

    public event EventHandler? Entered;

    public bool IsEnabled => _enabled;

    public void Apply(int heightPx)
    {
        _heightPx = Math.Clamp(heightPx, 1, 20);
        EnsureWindow();
        PositionWindow();
    }

    public void Enable()
    {
        if (_disposed)
        {
            return;
        }

        EnsureWindow();
        PositionWindow();
        // 幂等显隐：每次 MouseEnter 都 ShowWindow(SW_HIDE) 会产生 EVENT_OBJECT_HIDE，
        // 进而触发窗口枚举与 UI 刷新，形成不必要的事件回环。
        if (!_visible && _hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_SHOW);
            _visible = true;
        }

        _enabled = true;
    }

    public void Disable()
    {
        if (_visible && _hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_HIDE);
            _visible = false;
        }

        _enabled = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disable();
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

    private void EnsureWindow()
    {
        if (_disposed || _hwnd != IntPtr.Zero)
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
                    int error = Marshal.GetLastWin32Error();
                    _log.Warn(Module, $"注册热区窗口类失败 Win32Error={error}，自动隐藏降级为不可用");
                    return;
                }
            }
        }

        _hwnd = CreateWindowExW(ExStyle, ClassName, "WinDock hot zone", WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, _moduleHandle, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            _log.Warn(Module, $"创建热区窗口失败 Win32Error={Marshal.GetLastWin32Error()}");
            return;
        }

        lock (StaticSync)
        {
            Instances[_hwnd] = this;
        }

        // alpha=1 视觉上完全隐形，但保留命中测试；失败只记日志，不阻断功能。
        if (!SetLayeredWindowAttributes(_hwnd, 0, 1, LWA_ALPHA))
        {
            _log.Warn(Module, $"热区透明属性设置失败 Win32Error={Marshal.GetLastWin32Error()}");
        }
    }

    private void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        IntPtr monitor = MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            _log.Warn(Module, $"读取主显示器信息失败 Win32Error={Marshal.GetLastWin32Error()}，热区使用虚拟屏幕尺寸");
            info.Monitor = new RECT
            {
                Left = 0,
                Top = 0,
                Right = GetSystemMetrics(SM_CXSCREEN),
                Bottom = GetSystemMetrics(SM_CYSCREEN),
            };
        }

        // D5：用显示器绝对 rect，不能硬编码 (0,0)，本机副屏是负坐标。
        int x = info.Monitor.Left;
        int y = info.Monitor.Bottom - _heightPx;
        int width = info.Monitor.Right - info.Monitor.Left;
        // 不用 SWP_SHOWWINDOW：可见性只由 Enable/Disable 控制，保证幂等。
        if (!SetWindowPos(_hwnd, new IntPtr(HWND_TOPMOST), x, y, width, _heightPx,
                SWP_NOACTIVATE))
        {
            _log.Warn(Module, $"定位热区窗口失败 Win32Error={Marshal.GetLastWin32Error()}");
        }
    }

    private void OnMouseMove()
    {
        if (!_enabled)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if ((now - _lastEnterUtc).TotalMilliseconds < 200)
        {
            return; // WM_MOUSEMOVE 会连续触发，节流到 5 次/秒足够
        }

        _lastEnterUtc = now;
        Entered?.Invoke(this, EventArgs.Empty);
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_MOUSEMOVE)
        {
            HotZone? zone;
            lock (StaticSync)
            {
                Instances.TryGetValue(hwnd, out zone);
            }

            zone?.OnMouseMove();
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
