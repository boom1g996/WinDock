using System.IO;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Services;

namespace WinDock.Services;

/// <summary>
/// 托盘入口（WD-011 / FR-501）：M2 第五批（WD-034 / K-01）由 WinForms NotifyIcon
/// 换成 Core 层纯 Win32 <see cref="TrayIconService"/>，移除 WinForms/System.Drawing
/// 模块映射（实测约 20MB），菜单为系统原生 TrackPopupMenu 风格。
/// </summary>
public sealed class TrayService : IDisposable
{
    private const string Module = "Tray";

    private readonly IConfigService _config;
    private readonly ILogService _log;
    private readonly ITaskbarInterop _taskbar;
    private readonly MainWindow _dock;
    private readonly Action _openSettings;
    private readonly Action _openGuide;
    private readonly TrayIconService _tray;

    public TrayService(IConfigService config, ILogService log, ITaskbarInterop taskbar,
        MainWindow dock, Action openSettings, Action openGuide)
    {
        _config = config;
        _log = log;
        _taskbar = taskbar;
        _dock = dock;
        _openSettings = openSettings;
        _openGuide = openGuide;

        _tray = new TrayIconService(log, BuildMenu, openSettings);
        _tray.Show(Loc.Instance.T("Tray_Tip"));
    }

    /// <summary>
    /// WD-020：读取看门狗留下的恢复事件，弹一次托盘气泡后删除标记。
    /// 守护进程本身不创建窗口/托盘（D8 不提权、不注入），提示由主进程下次启动补上。
    /// </summary>
    public void NotifyRecovery()
    {
        WatchdogRecoveryEvent? recovery = WatchdogProtocol.ReadRecovery();
        if (recovery is null)
        {
            return;
        }

        WatchdogProtocol.ClearRecovery();
        _log.Warn(Module,
            $"上次异常退出已由看门狗处理：pid={recovery.Pid}, reason={recovery.Reason}, "
            + $"taskbarRestored={recovery.TaskbarRestored}, snapshot={recovery.SnapshotPresent}");
        _tray.ShowBalloon(Loc.Instance.T("Tray_RecoveryTitle"),
            Loc.Instance.T("Tray_RecoveryText"), warning: true);
    }

    public void ApplyConfig(DockConfig config)
    {
        // 托盘图标永远保留一个最小可见性，否则「退出」也无处可点（配置 showTrayIcon=false 时
        // 改为隐藏图标但保留进程，用户仍可用 Ctrl+Alt+D 或直接结束进程）。
        if (config.Behavior.ShowTrayIcon)
        {
            _tray.Show(Loc.Instance.T("Tray_Tip"));
        }
        else
        {
            _tray.Hide();
        }
    }

    public void Dispose()
    {
        _tray.Dispose();
    }

    /// <summary>菜单每次弹出时重建：隐藏/显示 Dock 的文本随状态刷新。</summary>
    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        return new List<TrayMenuItem>
        {
            new(Loc.Instance.T(_dock.IsDockVisible ? "Dock_Hide" : "Dock_Show"), ToggleDock),
            new(Loc.Instance.T("Tray_RestoreTaskbar"), RestoreTaskbar),
            new(string.Empty, () => { }) { IsSeparator = true },
            new(Loc.Instance.T("Tray_Settings"), () => _openSettings()),
            new(Loc.Instance.T("Tray_Guide"), () => _openGuide()),
            new(Loc.Instance.T("Tray_Exit"), () => System.Windows.Application.Current.Shutdown()),
        };
    }

    private void ToggleDock()
    {
        if (_dock.IsDockVisible)
        {
            _dock.HideDock();
        }
        else
        {
            _dock.ShowDock();
        }
    }

    private void RestoreTaskbar()
    {
        bool ok = _taskbar.Restore();
        _log.Info(Module, ok
            ? Loc.Instance.T("Log_TaskbarRestored")
            : "恢复原生任务栏未成功，请检查日志");
    }
}
