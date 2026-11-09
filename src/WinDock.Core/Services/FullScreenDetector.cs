using System.Runtime.InteropServices;
using System.Threading;
using WinDock.Core.Interfaces;
using static WinDock.Core.Native.NativeMethods;

namespace WinDock.Core.Services;

/// <summary>
/// 全屏 / 演示模式检测（WD-015 / D7 / FR-603）：QUNS 状态 + 前台窗口覆盖整屏双判据。
///
/// 以事件驱动为主：前台切换、最小化/还原、窗口尺寸变化结束（F11 / 全屏播放器
/// 必然 resize）都会立即判定，满足 AC-08「立即隐藏」。
/// SHQueryUserNotificationState 没有公开的状态变更通知，因此保留兜底轮询。
/// WD-033 实测（归因数据）后两项优化：
/// - 兜底轮询改**后台线程**（专用 Timer，不走 ITimerScheduler、不碰 UI 线程），
///   只有状态翻转才 Post UI —— 旧实现把 Check 派发到 UI 会周期性唤醒消息泵；
/// - 轮询周期**自适应退避**：状态稳定时 3s → 6s → 12s 封顶（每次唤醒都被进程
///   CPU 记账计一个 15.6ms 刻度，固定 3s 会把稳态推高，K-13）；
///   任何事件或状态翻转立即回到 3s，保证 AC-08 的反应速度由事件路径承担。
/// 事件回调合并为一次 UI 派发，避免事件洪泛。
/// </summary>
public sealed class FullScreenDetector : IFullScreenDetector
{
    private const string Module = "FullScreen";
    private const int PollBaseMs = 3000;
    private const int PollMaxMs = 12000;

    private readonly ILogService _log;
    private readonly IMetricsService? _metrics;
    private readonly SynchronizationContext? _uiContext;
    private readonly List<IntPtr> _hooks = new();
    private readonly object _stateSync = new();
    private WinEventProc? _hookProc;
    private System.Threading.Timer? _pollTimer;
    private int _checkPending;
    private int _pollIntervalMs = PollBaseMs;
    private int _stablePolls;
    private bool _running;

    public FullScreenDetector(ILogService log,
        SynchronizationContext? uiContext = null, IMetricsService? metrics = null)
    {
        _log = log;
        _uiContext = uiContext ?? SynchronizationContext.Current;
        _metrics = metrics;
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

        Check(); // 启动时在调用方线程（UI）先判定一次
        _pollIntervalMs = PollBaseMs;
        _stablePolls = 0;
        SchedulePoll();
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _pollTimer?.Dispose();
        _pollTimer = null;        foreach (IntPtr hook in _hooks)
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            CheckCore();
        }
        finally
        {
            _metrics?.Record("fullscreen.check", sw.Elapsed.TotalMilliseconds);
        }
    }

    private void CheckCore()
    {
        lock (_stateSync)
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
            ResetPollBackoff(); // 状态翻转后回到高频兜底，观察是否再次翻转
            _log.Info(Module, full
                ? $"进入全屏/演示态（QUNS={quns}, rectHit={rectHit}），Dock 将隐藏并禁用热区"
                : "退出全屏/演示态，准备恢复 Dock");

            // 兜底轮询可能在后台线程触发；订阅方（AutoHideController / HotZone）
            // 状态不是线程安全的，状态翻转必须回到 UI 线程派发。
            if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
            {
                _uiContext.Post(_ => StateChanged?.Invoke(this, full), null);
            }
            else
            {
                StateChanged?.Invoke(this, full);
            }
        }
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

        ResetPollBackoff(); // 有事件说明系统活跃，兜底立即回到高频
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

    /// <summary>按当前退避周期安排下一次兜底轮询（一次性 Timer，到期后重排）。</summary>
    private void SchedulePoll()
    {
        if (!_running)
        {
            return;
        }

        _pollTimer?.Dispose();
        int interval = _pollIntervalMs;
        _pollTimer = new System.Threading.Timer(_ =>
        {
            if (!_running)
            {
                return;
            }

            // 后台线程判定；状态翻转才 Post UI。稳定时退避翻倍，封顶 12s。
            _stablePolls++;
            if (_stablePolls >= 2)
            {
                _stablePolls = 0;
                _pollIntervalMs = Math.Min(_pollIntervalMs * 2, PollMaxMs);
            }

            Check();
            SchedulePoll();
        }, null, interval, Timeout.Infinite);
    }

    /// <summary>事件到达或状态翻转时把退避周期重置回 3s（事件路径承担反应速度）。</summary>
    private void ResetPollBackoff()
    {
        _pollIntervalMs = PollBaseMs;
        _stablePolls = 0;
    }
}
