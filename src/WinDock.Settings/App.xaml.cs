using System.Threading;
using System.Windows;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Services;

namespace WinDock.Settings;

/// <summary>设置窗口进程入口；与主进程通过 config.json + FileSystemWatcher 通信。</summary>
public partial class App : Application
{
    private const string MutexName = "WinDock.Settings.SingleInstance.v1";

    private Mutex? _mutex;
    private IConfigService? _config;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        ILogService log = new LogService();
        _config = new ConfigService(log);
        Loc.Instance.SetLanguage(_config.Current.General.Language);

        var window = new WinDock.Settings.MainWindow(_config, log);
        Application.Current.MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _config?.Dispose();
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        base.OnExit(e);
    }
}
