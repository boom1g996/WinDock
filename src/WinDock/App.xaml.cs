using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
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

    // 6.7 多显示器：副屏 Dock 实例（每屏一条，常驻显示，不参与自动隐藏/热区）。
    private readonly List<SecondaryDock> _extraDocks = new();
    private string _extraDockSignature = "";
    private IIconService? _iconsCache;
    private IAppLauncher? _launcherCache;

    /// <summary>设置窗口进程 PID 集合；用于在其全部退出后解除 Dock 的自动隐藏抑制。</summary>
    private readonly HashSet<int> _settingsPids = new();
    private readonly object _settingsLock = new();

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

    // M2 第五批：本地性能打点与 CPU 突发归因（WD-033）
    private IMetricsService? _metrics;

    // 6.13 模式 A：系统托盘溢出面板检测（3s 一次 FindWindow）
    private DispatcherTimer? _overflowWatch;

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
            PostToDockHosts("WinDock.RequestExit.v1");
            Shutdown(0);
            return;
        }

        if (Array.Exists(e.Args,
                a => string.Equals(a, "--elevate", StringComparison.OrdinalIgnoreCase)))
        {
            // R5「以管理员重启 Dock」的引导进程：普通权限运行，只做三件事——
            // 请求现有实例优雅退出 → 等它释放单实例互斥 → 以 runas 拉起新实例。
            // 反过来做（先提权启动）新实例会撞互斥直接退出，所以顺序不能换。
            _log = new LogService();
            Loc.Instance.SetLanguage("zh-CN");
            PostToDockHosts("WinDock.RequestExit.v1");
            if (!ElevationService.WaitForOtherInstances(TimeSpan.FromSeconds(8)))
            {
                _log.Warn("App", "提权重启前等待旧实例退出超时，仍尝试启动新实例");
            }

            bool ok = ElevationService.StartSelfElevated(_log);
            Shutdown(ok ? 0 : 1);
            return;
        }

        if (Array.Exists(e.Args,
                a => a.StartsWith("--dump-layout", StringComparison.OrdinalIgnoreCase)))
        {
            // 自动化用的只读探针：让常驻实例把图标屏幕坐标写进日志，本进程立即退出。
            // nonce 由调用方给定并原样回写到日志，避免自动化读到上一次请求的旧行。
            string? arg = Array.Find(e.Args,
                a => a.StartsWith("--dump-layout", StringComparison.OrdinalIgnoreCase));
            long nonce = 0;
            int eq = arg?.IndexOf('=') ?? -1;
            if (eq > 0)
            {
                _ = long.TryParse(arg![(eq + 1)..], out nonce);
            }

            PostToDockHosts("WinDock.DumpLayout.v1", nonce);
            Shutdown(0);
            return;
        }

        if (Array.Exists(e.Args,
                a => a.StartsWith("--dump-preview", StringComparison.OrdinalIgnoreCase)))
        {
            // WD-037 自动化探针：让常驻实例把预览面板每个关闭按钮的屏幕坐标
            // 写进日志（带 nonce 防读到旧行），本进程立即退出。
            string? arg = Array.Find(e.Args,
                a => a.StartsWith("--dump-preview", StringComparison.OrdinalIgnoreCase));
            long nonce = 0;
            int eq = arg?.IndexOf('=') ?? -1;
            if (eq > 0)
            {
                _ = long.TryParse(arg![(eq + 1)..], out nonce);
            }

            PostToDockHosts("WinDock.DumpPreview.v1", nonce);
            Shutdown(0);
            return;
        }

        // 自检必须在单实例互斥**之前**处理：否则 Dock 正在运行时 `--selftest`
        // 会被互斥分支直接 Shutdown(0)，一条断言都没跑却返回 0，形成"自检假通过"
        // （2026-11-10 实测：结果文件停在上一版，误以为新断言已通过）。
        if (Array.Exists(e.Args,
                a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            _log = new LogService();
            Loc.Instance.SetLanguage("zh-CN");
            Shutdown(SelfTest.Run(_log));
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

        _config = new ConfigService(_log);
        Loc.Instance.SetLanguage(_config.Current.General.Language);
        _log.Info("App", Loc.Instance.T("Log_Startup"));

        // WD-033：本地打点（8.6 不上报）；1Hz CPU 采样跑在独立后台线程，
        // 归因工具本身不唤醒 UI，避免污染被测对象。
        _metrics = new MetricsService(_log);
        _metrics.Mark("startup.config_loaded");
        if (_config.Current.Behavior.MetricsEnabled)
        {
            _metrics.StartCpuSampler();
        }

        _taskbar = new TaskbarInterop(_log);
        if (_taskbar.HasPendingRestore)
        {
            // WD-012：上次运行被强杀 / 崩溃，快照还在磁盘上，启动时立即恢复
            _taskbar.Restore();
            _log.Warn("App", "检测到未恢复的任务栏快照，已自动恢复（崩溃兜底）");
        }

        ApplyTaskbarMode(); // WD-025：启动即按配置应用共存模式

        _enumerator = new WindowEnumerator(_log, SynchronizationContext.Current, _metrics);
        // WD-018：磁盘缓存按配置开关注入；关闭时退化为 M1 的纯内存缓存。
        IIconService icons = new IconService(_log,
            _config.Current.Behavior.IconCacheEnabled ? new IconCache(_log) : null);
        _iconsCache = icons;
        _animation = new AnimationEngine();
        IAppLauncher launcher = new AppLauncher(_log, _metrics);
        _launcherCache = launcher;
        _preview = new PreviewService(_log);
        _guide = new StartupGuide(_config, _log);
        IDockDragService drag = new DockDragService(_config, _log);
        _drag = drag;

        _dock = new MainWindow(_config, _log, _taskbar, _enumerator,
            icons, _animation, launcher, _metrics);
        _tray = new TrayService(_config, _log, _taskbar, _dock, OpenSettings, OpenGuide);

        // M2 组合根：所有新增能力按接口组装，模块之间不互相引用实现（需求 14.2）。
        // 延时只走这一个 TimerScheduler，动效只走这一个 AnimationEngine（M2 D3）。
        _timers = new TimerScheduler(SynchronizationContext.Current, _log);
        _dock.BindPreview(_preview, _timers);
        _dock.BindDrag(drag, _timers);
        _hotZone = new HotZone(_log);
        _fullScreen = new FullScreenDetector(_log, SynchronizationContext.Current, _metrics);
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
        ApplyExtraDocks(_config.Current); // 6.7 每屏一条 Dock（可选）
        _metrics?.Mark("startup.first_show");
        _tray.ApplyConfig(_config.Current);
        StartupRegistry.Apply(_config.Current.General.LaunchAtStartup, _log);
        _fullScreen.Start();

        // WD-020：默认开启；exe 缺失时服务内部降级为关闭并写 WARN，不阻断 Dock。
        _watchdog = new WatchdogService(_log, _timers, _metrics);
        _watchdog.ApplyConfig(_config.Current);
        _tray?.NotifyRecovery(); // 上次异常退出由看门狗留下的提示，读完即删

        // R9：启动时检测第三方任务栏美化工具（StartAllBack 等）并提示一次。
        // 只提示、不自动改用户设置；扫描要枚举上千条卸载项，放后台线程避免拖慢启动，
        // 检测本身失败也绝不阻断启动。
        Task.Run(ConflictDetector.Detect).ContinueWith(task =>
        {
            if (task.Status != TaskStatus.RanToCompletion)
            {
                _log?.Warn("App", "第三方美化工具检测失败：" + task.Exception?.GetBaseException().Message);
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _tray?.NotifyConflicts(task.Result);
                }
                catch (Exception ex)
                {
                    // 退出竞态期间托盘可能已销毁，提示失败不影响任何功能。
                    _log?.Warn("App", "冲突提示失败：" + ex.Message);
                }
            }));
        }, TaskScheduler.Default);

        if (_guide?.ShouldShow(_config.Current) == true)
        {
            // 冷启动 350ms 后弹引导；期间 Hold 住 Dock，避免被 800ms 自动隐藏抢先滑出。
            _autoHide?.SetHeld(GuideHoldReason, true);
            _timers.Schedule(GuideShowKey, 350, ShowGuide);
        }
    }

    /// <summary>
    /// 向所有 DockHost 进程的顶层窗口定向投递注册消息。
    /// 不能用 HWND_BROADCAST：自动隐藏后 Dock 窗口滑出屏幕且被隐藏，广播到不了。
    /// </summary>
    private static void PostToDockHosts(string messageName, long wParam = 0)
    {
        uint message = NativeMethods.RegisterWindowMessage(messageName);
        IntPtr payload = (IntPtr)wParam;
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
                    NativeMethods.PostMessage(hwnd, message, payload, IntPtr.Zero);
                }

                return true;
            }, IntPtr.Zero);
            process.Dispose();
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

        CloseExtraDocks();
        _watchdog?.Stop();    // 先发 S 帧，避免看门狗把正常退出误判为崩溃
        _watchdog?.Dispose();
        _autoHide?.Dispose();
        _preview?.Dispose(); // FR-404：退出时兜底注销全部 DWM 缩略图句柄
        _fullScreen?.Dispose();
        _overflowWatch?.Stop();
        _overflowWatch = null;
        _hotkey?.Dispose();
        _hotZone?.Dispose();
        _timers?.Dispose();
        _tray?.Dispose();
        _enumerator?.Dispose();
        _animation?.Dispose();
        _metrics?.Dispose(); // 最后停采样，退出前的清理工作也纳入归因
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

    /// <summary>
    /// 6.7 多显示器：按配置为「除主 Dock 所在屏之外」的每块屏各开一条 Dock。
    /// 副屏 Dock 常驻显示（不绑定自动隐藏/热区），避免两条热区在同一屏幕边缘互相抢事件；
    /// 位置、尺寸、透明度仍与主 Dock 共享同一份配置，因此视觉完全一致。
    /// </summary>
    private void ApplyExtraDocks(DockConfig config)
    {
        if (_dock is null || _config is null || _iconsCache is null || _launcherCache is null)
        {
            return;
        }

        if (!config.Dock.PerMonitorDocks)
        {
            if (_extraDocks.Count > 0)
            {
                CloseExtraDocks();
                _log?.Info("App", "多屏 Dock 已关闭（perMonitorDocks=false）");
            }

            _extraDockSignature = "";
            return;
        }

        IReadOnlyList<DisplayMonitor> monitors = MonitorService.GetMonitors();
        string primaryId = config.Dock.MonitorIds?.FirstOrDefault() ?? "primary";
        int primaryIndex = -1;
        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].IsPrimary)
            {
                primaryIndex = i;
                break;
            }
        }

        int primaryDockIndex = primaryId == "primary" || !int.TryParse(primaryId, out int parsed)
            ? primaryIndex
            : parsed - 1;

        var wanted = new List<string>();
        for (int i = 0; i < monitors.Count; i++)
        {
            if (i == primaryDockIndex)
            {
                continue;
            }

            wanted.Add((i + 1).ToString()); // ResolveWorkArea 的编号从 1 开始
        }

        string signature = string.Join(",", wanted);
        if (string.Equals(signature, _extraDockSignature, StringComparison.Ordinal))
        {
            foreach (SecondaryDock dock in _extraDocks)
            {
                dock.Dock.ApplyConfig(config); // 保留同一批窗口，只热更新配置
            }

            return;
        }

        CloseExtraDocks();
        _extraDockSignature = signature;
        foreach (string monitorId in wanted)
        {
            var animation = new AnimationEngine();
            var window = new MainWindow(_config, _log!, _taskbar!, _enumerator!, _iconsCache,
                animation, _launcherCache, _metrics, monitorId);
            window.Show();
            _extraDocks.Add(new SecondaryDock(window, animation));
            _log?.Info("App", $"已在显示器 {monitorId} 上创建副屏 Dock");
        }
    }

    private void CloseExtraDocks()
    {
        foreach (SecondaryDock dock in _extraDocks)
        {
            try
            {
                dock.Dock.CloseForShutdown();
                dock.Animation.Dispose();
            }
            catch (InvalidOperationException)
            {
                // 窗口已关闭
            }
        }

        _extraDocks.Clear();
        _extraDockSignature = "";
    }

    private sealed record SecondaryDock(MainWindow Dock, IAnimationEngine Animation);

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
        ApplyExtraDocks(args.Config);        // 6.7 多屏 Dock 随配置增减
        ApplyTaskbarMode();                   // WD-025：共存模式切换即时生效
        if (_log is not null)
        {
            StartupRegistry.Apply(args.Config.General.LaunchAtStartup, _log);
        }
    }

    /// <summary>
    /// WD-025 / 6.13：按配置应用与原生任务栏的共存方式。
    /// - replace：隐藏原生任务栏，Dock 接管；失败时安全回退到共存。
    /// - coexist：保留原生任务栏（托盘区方案）；Dock 作为附加条。
    /// - beautify：完全不改动原生任务栏（默认、最安全）。
    /// 免责：模式 A/B 依赖非官方方式操作任务栏，系统更新可能失效；任何失败都回退共存。
    /// </summary>
    private void ApplyTaskbarMode()
    {
        if (_taskbar is null || _config is null)
        {
            return;
        }

        string mode = _config.Current.General.TaskbarMode;
        try
        {
            if (string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase))
            {
                _taskbar.ApplyCompactMode(false); // 模式 A 不需要精简原生任务栏
                bool ok = _taskbar.Hide();
                _log?.Info("App", ok
                    ? "任务栏模式=替换：已隐藏原生任务栏"
                    : "任务栏模式=替换：隐藏失败，已保持共存（安全回退）");
            }
            else if (string.Equals(mode, "coexist", StringComparison.OrdinalIgnoreCase))
            {
                _taskbar.Restore();
                // 6.13 模式 B：原生任务栏只留托盘区（隐藏搜索/任务视图/小组件/聊天）
                _taskbar.ApplyCompactMode(true);
                _log?.Info("App", "任务栏模式=精简共存：保留原生任务栏（托盘区）");
            }
            else
            {
                _taskbar.Restore();
                _taskbar.ApplyCompactMode(false);
                _log?.Info("App", "任务栏模式=仅美化：保留原生任务栏");
            }
        }
        catch (Exception ex)
        {
            _log?.Error("App", "应用任务栏共存模式失败", ex);
            _taskbar.Restore();
        }

        // 6.13：模式 A 下原生任务栏被隐藏，系统托盘随之不可用。
        // 用户一旦打开"溢出面板"，提示切到模式 B（精简共存，保留托盘区）。
        if (string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase))
        {
            StartOverflowWatch();
        }
        else
        {
            _overflowWatch?.Stop();
        }
    }

    /// <summary>
    /// 6.13 托盘溢出面板检测：3s 一次 FindWindow（开销可忽略），命中即提示并停止。
    /// 只在模式 A 下启动，避免不必要的轮询。
    /// </summary>
    private void StartOverflowWatch()
    {
        if (_overflowWatch is not null)
        {
            if (!_overflowWatch.IsEnabled)
            {
                _overflowWatch.Start();
            }

            return;
        }

        _overflowWatch = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _overflowWatch.Tick += (_, _) =>
        {
            bool open = NativeMethods.FindWindowW("TopLevelWindowForOverflowXamlIsland", null) != IntPtr.Zero
                        || NativeMethods.FindWindowW("NotifyIconOverflowWindow", null) != IntPtr.Zero;
            if (!open)
            {
                return;
            }

            _overflowWatch?.Stop();
            _log?.Info("App", "检测到系统托盘溢出面板：提示切换到精简共存模式");
            _tray?.NotifyTrayOverflow(SwitchToCoexistMode);
        };
        _overflowWatch.Start();
    }

    /// <summary>气泡点击：从模式 A 切到模式 B（保留原生任务栏托盘区）。</summary>
    private void SwitchToCoexistMode()
    {
        try
        {
            if (_config is null)
            {
                return;
            }

            DockConfig config = _config.Current;
            config.General.TaskbarMode = "coexist";
            _config.Save(config); // 触发 OnConfigChanged → ApplyTaskbarMode
            _log?.Info("App", "已切换到精简共存模式（保留系统托盘）");
        }
        catch (Exception ex)
        {
            _log?.Error("App", "切换到共存模式失败", ex);
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

            // 已有设置窗口在跑：直接聚焦，不重复启动；并确保护持 Dock 可见。
            Process[] running = Process.GetProcessesByName("WinDock.Settings");
            if (running.Length == 0)
            {
                Process? started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                if (started is null)
                {
                    _log?.Warn("App", "启动设置程序返回空句柄");
                    return;
                }

                AttachSettingsProcess(started);
            }
            else
            {
                foreach (Process p in running)
                {
                    AttachSettingsProcess(p);
                }

                // 把已有设置窗口提到前台，便于用户操作。
                if (running[0].MainWindowHandle != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(running[0].MainWindowHandle);
                }
            }

            // 设置打开期间抑制自动隐藏，保证 Dock 常驻可见（AC-09 实时预览）。
            _dock?.SetSettingsHeld(true);
        }
        catch (Exception ex)
        {
            _log?.Error("App", "打开设置窗口失败", ex);
        }
    }

    /// <summary>挂接设置进程的退出事件；所有设置进程都退出后，解除 Dock 的自动隐藏抑制。</summary>
    private void AttachSettingsProcess(Process process)
    {
        lock (_settingsLock)
        {
            if (!_settingsPids.Add(process.Id))
            {
                return; // 已跟踪，避免重复挂接
            }
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            bool anyLeft;
            lock (_settingsLock)
            {
                _settingsPids.Remove(process.Id);
                anyLeft = _settingsPids.Count > 0;
            }

            if (!anyLeft)
            {
                _dock?.SetSettingsHeld(false);
                _log?.Info("App", "设置窗口已关闭：恢复 Dock 自动隐藏");
            }
        };
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
