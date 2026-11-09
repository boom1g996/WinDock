using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinDock.Core;
using WinDock.Core.Interfaces;
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

    private Mutex? _mutex;
    private ILogService? _log;
    private IConfigService? _config;
    private TaskbarInterop? _taskbar;
    private WindowEnumerator? _enumerator;
    private IAnimationEngine? _animation;
    private TrayService? _tray;
    private MainWindow? _dock;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        IIconService icons = new IconService(_log);
        _animation = new AnimationEngine();
        IAppLauncher launcher = new AppLauncher(_log);

        _dock = new MainWindow(_config, _log, _taskbar, _enumerator,
            icons, _animation, launcher);
        _tray = new TrayService(_config, _log, _taskbar, _dock, OpenSettings);

        _enumerator.WindowsChanged += (_, _) => _dock.RefreshItems();
        _config.Changed += OnConfigChanged;
        _taskbar.TaskbarRecreated += OnTaskbarRecreated;

        _enumerator.Start();
        _dock.RefreshItems();
        _dock.Show();
        _tray.ApplyConfig(_config.Current);
        StartupRegistry.Apply(_config.Current.General.LaunchAtStartup, _log);
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
        Loc.Instance.SetLanguage(args.Config.General.Language);
        _dock?.ApplyConfig(args.Config);
        _tray?.ApplyConfig(args.Config);
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
