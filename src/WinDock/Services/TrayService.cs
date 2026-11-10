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
        // 5.2「托盘气泡 + 一键恢复」：点击气泡再补做一次恢复原生任务栏，
        // 覆盖"看门狗恢复失败/用户又把任务栏弄丢"的情况（操作幂等）。
        _tray.ShowBalloon(Loc.Instance.T("Tray_RecoveryTitle"),
            Loc.Instance.T("Tray_RecoveryText"), warning: true, onClick: RestoreTaskbar);
    }

    /// <summary>R9：检测到第三方任务栏美化工具时提示一次（不自动改用户设置）。</summary>
    public void NotifyConflicts(IReadOnlyList<ConflictInfo> conflicts)
    {
        if (conflicts.Count == 0)
        {
            return;
        }

        string names = ConflictDetector.Describe(conflicts);
        _log.Warn(Module, "检测到可能冲突的第三方美化工具：" + names);
        _tray.ShowBalloon(Loc.Instance.T("Tray_ConflictTitle"),
            Loc.Instance.T("Tray_ConflictText", names), warning: true);
    }

    /// <summary>
    /// 6.13 模式 A 下用户打开了系统托盘溢出面板：模式 A 隐藏了原生任务栏，
    /// 托盘就无处可放，提示切到模式 B（精简共存）。点击气泡即切换。
    /// </summary>
    public void NotifyTrayOverflow(Action switchToCoexist)
    {
        _tray.ShowBalloon(Loc.Instance.T("Tray_OverflowTitle"),
            Loc.Instance.T("Tray_OverflowText"), warning: false, onClick: switchToCoexist);
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
            new(Loc.Instance.T("Tray_RestartElevated"), RestartElevated),
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

    /// <summary>
    /// AC-10：托盘「恢复原生任务栏」后原生任务栏 3s 内恢复，且 Dock 退出。
    /// 仅在确实接管过任务栏（replace / coexist）时才退出并回写模式，
    /// 模式 C（仅美化）下本操作退化为幂等兜底，不应把正在用的 Dock 关掉。
    /// </summary>
    private void RestoreTaskbar()
    {
        string mode = _config.Current.General.TaskbarMode;
        bool ok = _taskbar.Restore();
        _log.Info(Module, ok
            ? Loc.Instance.T("Log_TaskbarRestored")
            : "恢复原生任务栏未成功，请检查日志");

        if (string.Equals(mode, "beautify", StringComparison.Ordinal))
        {
            return;
        }

        // 回写模式：否则下次启动或配置热更新会把任务栏再隐藏一次。
        DockConfig config = _config.Current;
        config.General.TaskbarMode = "beautify";
        _config.Save(config);
        _log.Info(Module, $"已恢复原生任务栏，任务栏模式回写为 beautify（原为 {mode}），Dock 即将退出");
        System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>
    /// R5：以管理员重启。先恢复原生任务栏再交棒，避免提权实例启动前
    /// 系统处于"任务栏被移出屏幕 + Dock 已退出"的真空期。
    /// 这里不走 <see cref="RestoreTaskbar"/>：那条路径会回写模式并退出进程。
    /// </summary>
    private void RestartElevated()
    {
        _taskbar.Restore();
        ElevationService.RequestRestartElevated(_log);
    }
}
