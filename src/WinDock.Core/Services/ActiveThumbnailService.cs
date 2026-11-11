using System.Windows;
using System.Windows.Threading;
using WinDock.Core.Interfaces;
using WinDock.Core.Native;

namespace WinDock.Core.Services;

/// <summary>
/// M5：任务栏左段的「当前窗口实况缩略图」。
/// </summary>
/// <remarks>
/// 和 <see cref="PreviewService"/> 分开实现，理由是两者生命周期完全不同：
/// 预览面板是「一次悬停一组句柄、移开全部注销」，而这里是「一个跟随前台的长生命周期句柄」。
/// 共用同一个服务会让 <c>Hide()</c> 的清空语义互相打断（关预览面板会把任务栏缩略图也注销）。
///
/// 目标窗口限制：必须是别的进程的顶层窗口，且不是 shell（桌面/任务栏）——
/// 点 Dock 自己会把前台切给 Dock，那种情况保留上一个有效目标，避免缩略图闪黑。
/// </remarks>
public sealed class ActiveThumbnailService : IDisposable
{
    private const string Module = "ActiveThumb";

    /// <summary>shell 的窗口类名：它们成为前台时不该被当作「当前窗口」。</summary>
    private static readonly string[] ShellClasses =
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow",
    };

    private readonly ILogService _log;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.WinEventProc _proc;

    private IntPtr _hook;
    private IntPtr _host;
    private IntPtr _source;
    private IntPtr _thumb;
    private Rect _destination;
    private bool _visible;
    private bool _disposed;

    public ActiveThumbnailService(ILogService log, Dispatcher dispatcher)
    {
        _log = log;
        _dispatcher = dispatcher;
        _proc = OnForegroundChanged;

        IsAvailable = NativeMethods.DwmIsCompositionEnabled(out int enabled) == 0
                      && enabled != 0
                      && NativeMethods.HasExport("dwmapi.dll", "DwmRegisterThumbnail")
                      && NativeMethods.HasExport("dwmapi.dll", "DwmUpdateThumbnailProperties");
        if (!IsAvailable)
        {
            _log.Warn(Module, "DWM 缩略图不可用，任务栏当前窗口缩略图退化为占位");
        }
    }

    public bool IsAvailable { get; }

    /// <summary>当前跟踪的窗口句柄；<see cref="IntPtr.Zero"/> 表示没有可用目标。</summary>
    public IntPtr TargetWindow => _source;

    /// <summary>目标窗口变化（UI 可据此刷新占位文字/是否可点）。</summary>
    public event EventHandler? TargetChanged;

    public void Attach(IntPtr host)
    {
        if (_disposed || host == IntPtr.Zero)
        {
            return;
        }

        _host = host;
        if (_hook == IntPtr.Zero)
        {
            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _proc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        }

        RefreshTarget();
    }

    /// <summary>目标矩形（宿主窗口客户区像素坐标）。布局变化后由 UI 重新给。</summary>
    public void SetDestination(Rect destinationPx, bool visible)
    {
        _destination = destinationPx;
        _visible = visible;
        Apply();
    }

    /// <summary>重新取前台窗口；不合适的目标直接跳过（保留旧目标）。</summary>
    public void RefreshTarget()
    {
        if (_disposed || _host == IntPtr.Zero)
        {
            return;
        }

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (!IsUsableTarget(foreground) || foreground == _source)
        {
            // 前台变成了 Dock 自己 / shell：不动现有缩略图，避免一开面板就闪黑。
            if (_source != IntPtr.Zero && !NativeMethods.IsWindow(_source))
            {
                SwitchTo(IntPtr.Zero);
            }

            return;
        }

        SwitchTo(foreground);
    }

    private static bool IsUsableTarget(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle)
            || !NativeMethods.IsWindowVisible(handle))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
        if (pid == (uint)Environment.ProcessId)
        {
            return false;
        }

        var buffer = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(handle, buffer, buffer.Capacity);
        string cls = buffer.ToString();
        foreach (string shell in ShellClasses)
        {
            if (string.Equals(cls, shell, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // 最小化窗口的 DWM 源不刷新，注册了也是黑块。
        return !NativeMethods.IsIconic(handle);
    }

    private void SwitchTo(IntPtr target)
    {
        Unregister();
        _source = target;
        if (target == IntPtr.Zero)
        {
            TargetChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsAvailable && _host != IntPtr.Zero)
        {
            int hr = NativeMethods.DwmRegisterThumbnail(_host, target, out IntPtr thumb);
            if (hr == 0 && thumb != IntPtr.Zero)
            {
                _thumb = thumb;
            }
            else
            {
                _log.Warn(Module, $"DwmRegisterThumbnail 失败 hr={NativeMethods.HResultText(hr)}");
            }
        }

        Apply();
        TargetChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Apply()
    {
        if (_disposed || _thumb == IntPtr.Zero)
        {
            return;
        }

        var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                      | NativeMethods.DWM_TNP_OPACITY
                      | NativeMethods.DWM_TNP_VISIBLE
                      | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new NativeMethods.RECT
            {
                Left = (int)Math.Round(_destination.Left),
                Top = (int)Math.Round(_destination.Top),
                Right = (int)Math.Round(_destination.Right),
                Bottom = (int)Math.Round(_destination.Bottom),
            },
            opacity = 255,
            fVisible = _visible && _destination.Width > 2 && _destination.Height > 2,
            fSourceClientAreaOnly = true,
        };

        int hr = NativeMethods.DwmUpdateThumbnailProperties(_thumb, ref props);
        if (hr != 0)
        {
            _log.Warn(Module, $"DwmUpdateThumbnailProperties 失败 hr={NativeMethods.HResultText(hr)}");
        }
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed)
        {
            return;
        }

        // 事件可能在别的线程回调；缩略图 API 线程无关，但事件订阅者要 UI 线程。
        if (_dispatcher.CheckAccess())
        {
            RefreshTarget();
        }
        else
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshTarget));
        }
    }

    private void Unregister()
    {
        if (_thumb == IntPtr.Zero)
        {
            return;
        }

        int hr = NativeMethods.DwmUnregisterThumbnail(_thumb);
        if (hr != 0)
        {
            _log.Warn(Module, $"DwmUnregisterThumbnail 失败 hr={NativeMethods.HResultText(hr)}");
        }

        _thumb = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        _source = IntPtr.Zero;
    }
}
