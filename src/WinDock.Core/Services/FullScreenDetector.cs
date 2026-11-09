using System.Runtime.InteropServices;
using WinDock.Core.Interfaces;
using static WinDock.Core.Native.NativeMethods;

namespace WinDock.Core.Services;

/// <summary>
/// 全屏 / 演示模式检测（WD-015 / D7 / FR-603）：QUNS 状态 + 前台窗口覆盖整屏双判据。
///
/// 以事件驱动为主：前台切换、最小化/还原、窗口尺寸变化结束（F11 / 全屏播放器
/// 必然 resize）都会立即判定，满足 AC-08「立即隐藏」。
/// SHQueryUserNotificationState 没有公开的状态变更通知，因此保留一个
/// **3s 兜底确认**（复用唯一 ITimerScheduler，不是独立 Timer / DispatcherTimer）；
/// 1s 版本实测把稳态空闲 CPU 推到 0.7%（需求 8.1 ≤0.3%），3s 仅为状态兜底。
/// 事件回调合并为一次 UI 派发，避免事件洪泛。
/// </summary>
public sealed class FullScreenDetector : IFullScreenDetector
{
    private const string Module = "FullScreen";
    private const string PollKey = "fullscreen.confirm";
    private const int PollIntervalMs = 3000;

    private readonly ILogService _log;
    private readonly ITimerScheduler _timers;
    private readonly SynchronizationContext? _uiContext;
    private readonly List<IntPtr> _hooks = new();
    private WinEventProc? _hookProc;
    private int _checkPending;
    private bool _running;

    public FullScreenDetector(ILogService log, ITimerScheduler timers,
        SynchronizationContext? uiContext = null)
    {
        _log = log;
        _timers = timers;
        _uiContext = uiContext ?? SynchronizationContext.Current;
    }

    public event EventHandler<bool>? StateChanged;

    public bool IsFullScreen { get; private set; }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _hookProc = OnWinEvent;
        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        AddHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, flags);       // 切换应用
        AddHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, flags);   // 最小化/还原
        AddHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND, flags);     // 全屏 resize

        Check();
        ScheduleNext();
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _timers.Cancel(PollKey);
        foreach (IntPtr hook in _hooks)
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWinEvent(hook);
            }
        }

        _hooks.Clear();
        _hookProc = null;
    }

    public void Dispose() => Stop();

    /// <summary>立即判定一次（也可供热区触发时手动复核）。</summary>
    public void Check()
    {
        bool qunsHit = false;
        int hr = SHQueryUserNotificationState(out int quns);
        if (hr == 0)
        {
            qunsHit = quns == QUNS_RUNNING_D3D_FULL_SCREEN
                      || quns == QUNS_PRESENTATION_MODE;
        }
        else
        {
            _log.Warn(Module, $"SHQueryUserNotificationState 失败 HRESULT={HResultText(hr)}");
        }

        bool rectHit = ForegroundCoversMonitor();
        bool full = qunsHit || rectHit;
        if (full == IsFullScreen)
        {
            return;
        }

        IsFullScreen = full;
        _log.Info(Module, full
            ? $"进入全屏/演示态（QUNS={quns}, rectHit={rectHit}），Dock 将隐藏并禁用热区"
            : "退出全屏/演示态，准备恢复 Dock");
        StateChanged?.Invoke(this, full);
    }

    private bool ForegroundCoversMonitor()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !GetWindowRect(foreground, out RECT rect))
        {
            return false;
        }

        IntPtr monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        // DWM 可见边框会让窗口 rect 比屏幕略大几像素，留 8px 容差。
        const int tolerance = 8;
        return rect.Left <= info.Monitor.Left + tolerance
               && rect.Top <= info.Monitor.Top + tolerance
               && rect.Right >= info.Monitor.Right - tolerance
               && rect.Bottom >= info.Monitor.Bottom - tolerance;
    }

    private void AddHook(uint min, uint max, uint flags)
    {
        IntPtr hook = SetWinEventHook(min, max, IntPtr.Zero, _hookProc!, 0, 0, flags);
        if (hook == IntPtr.Zero)
        {
            _log.Warn(Module,
                $"事件钩子 0x{min:X4}~0x{max:X4} 注册失败 Win32Error={Marshal.GetLastWin32Error()}");
            return;
        }

        _hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (!_running || Interlocked.Exchange(ref _checkPending, 1) == 1)
        {
            return; // 事件洪泛时合并为一次 UI 派发
        }

        if (_uiContext is not null)
        {
            _uiContext.Post(_ =>
            {
                Interlocked.Exchange(ref _checkPending, 0);
                if (_running)
                {
                    Check();
                }
            }, null);
        }
        else
        {
            Interlocked.Exchange(ref _checkPending, 0);
            Check();
        }
    }

    private void ScheduleNext()
    {
        if (!_running)
        {
            return;
        }

        _timers.Schedule(PollKey, PollIntervalMs, () =>
        {
            if (!_running)
            {
                return;
            }

            Check();
            ScheduleNext();
        });
    }
}
