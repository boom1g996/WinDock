using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Native;
using WinDock.Core.Services;
using WinDock.Services;

namespace WinDock;

/// <summary>
/// 唯一组合根（设计说明 1.3）：所有服务在此创建并按接口注入，模块实现之间互不引用。
/// 同时负责单实例、崩溃兜底恢复任务栏、配置热更新分发与退出清理。
/// </summary>
public partial class App : Application
{
    private const string MutexName = "WinDock.DockHost.SingleInstance.v1";
    private const string GuideHoldReason = "guide";
    private const string GuideShowKey = "guide.show";

    private Mutex? _mutex;
    private ILogService? _log;
    private IConfigService? _config;
    private TaskbarInterop? _taskbar;
    private WindowEnumerator? _enumerator;
    private IAnimationEngine? _animation;
    private TrayService? _tray;
    private MainWindow? _dock;

    // M2 第二批：自动隐藏 / 全屏检测 / 热区 / 快捷键 / 唯一延时调度器
    private ITimerScheduler? _timers;
    private IFullScreenDetector? _fullScreen;
    private IHotZone? _hotZone;
    private IAutoHideController? _autoHide;
    private IHotkeyService? _hotkey;

    // M2 第三批：窗口缩略图预览 / 首次启动引导
    private IPreviewService? _preview;
    private IStartupGuide? _guide;

    // M2 第四批：拖拽排序 / 独立进程看门狗
    private IDockDragService? _drag;
    private IWatchdogService? _watchdog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (Array.Exists(e.Args,
                a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase)))
        {
            // 安装器 / 冒烟脚本请求常驻实例优雅退出：向该进程的**每个顶层窗口**
            // 定向投递注册消息，由主窗口走 Application.Shutdown → OnExit →
            // WatchdogService.Stop() 发出 S 帧。App.xaml 为 OnExplicitShutdown，
            // WM_CLOSE 关主窗口不会结束进程；HWND_BROADCAST 实测到不了隐藏的
            // 自动隐藏窗口（18:00 实机），因此不能只广播。
            uint message = NativeMethods.RegisterWindowMessage("WinDock.RequestExit.v1");
            int self = Environment.ProcessId;
            foreach (Process process in Process.GetProcessesByName("DockHost"))
            {
                if (process.Id == self)
                {
                    process.Dispose();
                    continue;
                }

                uint target = (uint)process.Id;
                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out uint owner);
                    if (owner == target)
                    {
                        NativeMethods.PostMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero);
                    }

                    return true;
                }, IntPtr.Zero);
                process.Dispose();
            }

            Shutdown(0);
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        _log = new LogService();
        Loc.Instance.SetLanguage("zh-CN");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (Array.Exists(e.Args,
                a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(SelfTest.Run(_log));
            return;
        }

        _config = new ConfigService(_log);
        Loc.Instance.SetLanguage(_config.Current.General.Language);
        _log.Info("App", Loc.Instance.T("Log_Startup"));

        _taskbar = new TaskbarInterop(_log);
        if (_taskbar.HasPendingRestore)
        {
            // WD-012：上次运行被强杀 / 崩溃，快照还在磁盘上，启动时立即恢复
            _taskbar.Restore();
            _log.Warn("App", "检测到未恢复的任务栏快照，已自动恢复（崩溃兜底）");
        }

        _enumerator = new WindowEnumerator(_log, SynchronizationContext.Current);
        // WD-018：磁盘缓存按配置开关注入；关闭时退化为 M1 的纯内存缓存。
        IIconService icons = new IconService(_log,
            _config.Current.Behavior.IconCacheEnabled ? new IconCache(_log) : null);
        _animation = new AnimationEngine();
        IAppLauncher launcher = new AppLauncher(_log);
        _preview = new PreviewService(_log);
        _guide = new StartupGuide(_config, _log);
        IDockDragService drag = new DockDragService(_config, _log);
        _drag = drag;

        _dock = new MainWindow(_config, _log, _taskbar, _enumerator,
            icons, _animation, launcher);
        _tray = new TrayService(_config, _log, _taskbar, _dock, OpenSettings, OpenGuide);

        // M2 组合根：所有新增能力按接口组装，模块之间不互相引用实现（需求 14.2）。
        // 延时只走这一个 TimerScheduler，动效只走这一个 AnimationEngine（M2 D3）。
        _timers = new TimerScheduler(SynchronizationContext.Current, _log);
        _dock.BindPreview(_preview, _timers);
        _dock.BindDrag(drag, _timers);
        _hotZone = new HotZone(_log);
        _fullScreen = new FullScreenDetector(_log, _timers, SynchronizationContext.Current);
        _hotkey = new HotkeyService(_log);
        _autoHide = new AutoHideController(_log, _timers, _animation,
            _fullScreen, _hotZone, _dock);
        _dock.BindAutoHide(_autoHide, _hotkey);
        _fullScreen.StateChanged += (_, full) =>
        {
            if (_dock is not null)
            {
                _dock.AnimationsPaused = full;
            }
        };

        _enumerator.WindowsChanged += (_, _) => _dock.RefreshItems();
        _config.Changed += OnConfigChanged;
        _taskbar.TaskbarRecreated += OnTaskbarRecreated;

        _enumerator.Start();
        _dock.RefreshItems();
        _dock.Show(); // 触发 SourceInitialized → 注册 Ctrl+Alt+D
        _tray.ApplyConfig(_config.Current);
        StartupRegistry.Apply(_config.Current.General.LaunchAtStartup, _log);
        _fullScreen.Start();

        // WD-020：默认开启；exe 缺失时服务内部降级为关闭并写 WARN，不阻断 Dock。
        _watchdog = new WatchdogService(_log, _timers);
        _watchdog.ApplyConfig(_config.Current);
        _tray?.NotifyRecovery(); // 上次异常退出由看门狗留下的提示，读完即删

        if (_guide?.ShouldShow(_config.Current) == true)
        {
            // 冷启动 350ms 后弹引导；期间 Hold 住 Dock，避免被 800ms 自动隐藏抢先滑出。
            _autoHide?.SetHeld(GuideHoldReason, true);
            _timers.Schedule(GuideShowKey, 350, ShowGuide);
        }
    }

    /// <summary>显示引导窗口；按 reason 引用计数 Hold，完成后可靠释放。</summary>
    private void ShowGuide()
    {
        if (_guide is null || _autoHide is null)
        {
            return;
        }

        _autoHide.SetHeld(GuideHoldReason, true);
        _guide.Show(() => _autoHide.SetHeld(GuideHoldReason, false));
    }

    /// <summary>托盘「首次引导…」：随时可重新查看，不要求重置配置。</summary>
    private void OpenGuide()
    {
        if (_guide is null || _autoHide is null || _guide.IsVisible)
        {
            return;
        }

        _timers?.Cancel(GuideShowKey);
        ShowGuide();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // AC-10 / 8.2：退出 3s 内恢复原生任务栏；模式 C 下为 no-op（无快照）
            _taskbar?.Restore();
        }
        catch (Exception ex)
        {
            _log?.Error("App", "退出时恢复任务栏失败", ex);
        }

        _watchdog?.Stop();    // 先发 S 帧，避免看门狗把正常退出误判为崩溃
        _watchdog?.Dispose();
        _autoHide?.Dispose();
        _preview?.Dispose(); // FR-404：退出时兜底注销全部 DWM 缩略图句柄
        _fullScreen?.Dispose();
        _hotkey?.Dispose();
        _hotZone?.Dispose();
        _timers?.Dispose();
        _tray?.Dispose();
        _enumerator?.Dispose();
        _animation?.Dispose();
        _config?.Dispose();
        _log?.Info("App", Loc.Instance.T("Log_Exit"));

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        base.OnExit(e);
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs args)
    {
        // ConfigService 的 FileSystemWatcher 回调在线程池 Timer 线程上，
        // 而 ApplyConfig 会改 WPF 依赖对象（Capsule / 图标树）。
        // M2 第四批实机冒烟：外部改 config.json 曾因此抛 InvalidOperationException。
        Dispatcher.InvokeAsync(() => ApplyConfigOnUiThread(args));
    }

    private void ApplyConfigOnUiThread(ConfigChangedEventArgs args)
    {
        Loc.Instance.SetLanguage(args.Config.General.Language);
        _dock?.ApplyConfig(args.Config);
        _tray?.ApplyConfig(args.Config);
        _autoHide?.ApplyConfig(args.Config);
        _hotkey?.ApplyConfig(args.Config);
        _watchdog?.ApplyConfig(args.Config); // 开关 / 失联阈值热更新
        if (_log is not null)
        {
            StartupRegistry.Apply(args.Config.General.LaunchAtStartup, _log);
        }
    }

    private async void OnTaskbarRecreated(object? sender, EventArgs args)
    {
        try
        {
            // 8.2：Explorer 重启后 2s 内重新应用；仅模式 A（replace）需要重新隐藏
            await Task.Delay(1500);
            if (_config?.Current.General.TaskbarMode == "replace" && _taskbar is not null)
            {
                _taskbar.Restore();
                _taskbar.Hide();
                _log?.Info("App", "Explorer 重启后已重新应用任务栏隐藏");
            }
        }
        catch (Exception ex)
        {
            _log?.Error("App", "TaskbarCreated 重应用失败", ex);
        }
    }

    /// <summary>供 Dock 右键菜单与托盘双击打开设置窗口。</summary>
    public void ShowSettings() => OpenSettings();

    private void OpenSettings()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "WinDock.Settings.exe");
            if (!File.Exists(path))
            {
                _log?.Warn("App", $"未找到设置程序：{path}");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log?.Error("App", "打开设置窗口失败", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("App", Loc.Instance.T("Log_Unhandled"), e.Exception);
        TryRestoreAfterCrash();
        e.Handled = true; // 需求 8.2：禁止弹系统崩溃框
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (_log is not null && e.ExceptionObject is Exception exception)
        {
            _log.Error("App", Loc.Instance.T("Log_Unhandled"), exception);
        }

        TryRestoreAfterCrash();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("App", "未观察的任务异常", e.Exception);
        e.SetObserved();
    }

    private void TryRestoreAfterCrash()
    {
        try
        {
            _taskbar?.Restore();
        }
        catch (Exception ex)
        {
            _log?.Error("App", "异常兜底恢复任务栏失败", ex);
        }
    }
}
