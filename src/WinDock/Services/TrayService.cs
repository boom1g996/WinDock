using System.IO;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Services;

namespace WinDock.Services;

/// <summary>托盘图标与菜单（WD-011 / FR-501 的系统原生风格菜单由 ContextMenuStrip 提供）。</summary>
public sealed class TrayService : IDisposable
{
    private const string Module = "Tray";

    private readonly IConfigService _config;
    private readonly ILogService _log;
    private readonly ITaskbarInterop _taskbar;
    private readonly MainWindow _dock;
    private readonly Action _openSettings;
    private readonly Action _openGuide;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _toggleItem;

    public TrayService(IConfigService config, ILogService log, ITaskbarInterop taskbar,
        MainWindow dock, Action openSettings, Action openGuide)
    {
        _config = config;
        _log = log;
        _taskbar = taskbar;
        _dock = dock;
        _openSettings = openSettings;
        _openGuide = openGuide;

        _menu = new WinForms.ContextMenuStrip();
        _toggleItem = Item("Dock_Hide", ToggleDock);
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(Item("Tray_RestoreTaskbar", RestoreTaskbar));
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(Item("Tray_Settings", () => _openSettings()));
        _menu.Items.Add(Item("Tray_Guide", () => _openGuide()));
        _menu.Items.Add(Item("Tray_Exit", () => System.Windows.Application.Current.Shutdown()));

        _icon = new WinForms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = Loc.Instance.T("Tray_Tip"),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _openSettings();
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
        try
        {
            _icon.ShowBalloonTip(5000,
                Loc.Instance.T("Tray_RecoveryTitle"),
                Loc.Instance.T("Tray_RecoveryText"),
                WinForms.ToolTipIcon.Warning);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Warn(Module, $"托盘气泡显示失败：{ex.Message}");
        }
    }

    public void ApplyConfig(DockConfig config)
    {
        // 托盘图标永远保留一个最小可见性，否则「退出」也无处可点（配置 showTrayIcon=false 时
        // 改为隐藏图标但保留进程，用户仍可用 Ctrl+Alt+D 或直接结束进程）。
        _icon.Visible = config.Behavior.ShowTrayIcon;
        UpdateToggleText();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private WinForms.ToolStripMenuItem Item(string key, Action action)
    {
        var item = new WinForms.ToolStripMenuItem(Loc.Instance.T(key));
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log.Error(Module, $"托盘菜单动作失败：{key}", ex);
            }
        };
        return item;
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

        UpdateToggleText();
    }

    private void UpdateToggleText()
    {
        string key = _dock.IsDockVisible ? "Dock_Hide" : "Dock_Show";
        _toggleItem.Text = Loc.Instance.T(key);
    }

    private void RestoreTaskbar()
    {
        bool ok = _taskbar.Restore();
        _log.Info(Module, ok
            ? Loc.Instance.T("Log_TaskbarRestored")
            : "恢复原生任务栏未成功，请检查日志");
    }
}
