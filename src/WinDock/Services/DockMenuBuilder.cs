using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;

namespace WinDock.Services;

/// <summary>图标右键菜单（FR-501 结构）；使用 WPF 原生菜单样式，不做 macOS 圆角/字体。</summary>
public static class DockMenuBuilder
{
    public static ContextMenu Build(DockItem item, IConfigService config, ILogService log,
        IAppLauncher launcher, Action refresh, Action openSettings, Action hideDock)
    {
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem
        {
            Header = string.IsNullOrWhiteSpace(item.DisplayName)
                ? Loc.Instance.T("AppName")
                : item.DisplayName,
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        });
        menu.Items.Add(new Separator());

        if (item.Group is { Windows.Count: > 0 } group)
        {
            foreach (AppWindowInfo window in group.Windows.Take(8))
            {
                string suffix = window.IsForeground ? "  ●" : "";
                var windowItem = new MenuItem { Header = Truncate(window.Title, 42) + suffix };
                AppWindowInfo target = window;
                windowItem.Click += (_, _) =>
                {
                    if (!launcher.Activate(target))
                    {
                        log.Warn("Menu", Loc.Instance.T("Error_ActivateFailed"));
                    }
                };
                menu.Items.Add(windowItem);
            }
        }
        else
        {
            menu.Items.Add(new MenuItem
            {
                Header = Loc.Instance.T("Menu_NoWindows"),
                IsEnabled = false,
            });
        }

        menu.Items.Add(new Separator());

        // WD-029 / FR-501 P2：读取目标应用的 Jump List「最近/常用」项；读不到则不显示分组。
        if (!string.IsNullOrWhiteSpace(item.TargetPath) && File.Exists(item.TargetPath)
            && !item.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<WinDock.Core.Services.JumpListItem> jumps =
                WinDock.Core.Services.JumpListReader.Read(item.TargetPath);
            if (jumps.Count > 0)
            {
                var jumpRoot = new MenuItem { Header = Loc.Instance.T("Menu_JumpList") };
                foreach (WinDock.Core.Services.JumpListItem jump in jumps)
                {
                    var jumpItem = new MenuItem { Header = Truncate(jump.Title, 42) };
                    string target = jump.Path;
                    jumpItem.Click += (_, _) => OpenPath(target);
                    jumpRoot.Items.Add(jumpItem);
                }

                menu.Items.Add(jumpRoot);
            }
        }

        Add(menu, "Menu_NewWindow", () =>
        {
            if (item.Pinned is not null)
            {
                launcher.NewWindow(item.Pinned);
            }
        });

        if (item.Pinned is not null)
        {
            PinnedItem pinned = item.Pinned;
            Add(menu, "Menu_Unpin", () =>
            {
                config.Current.Pinned.Remove(pinned);
                config.Save(config.Current);
                refresh();
            });
        }
        else if (!string.IsNullOrWhiteSpace(item.TargetPath))
        {
            Add(menu, "Menu_Pin", () =>
            {
                config.Current.Pinned.Add(new PinnedItem
                {
                    Type = "app",
                    Path = item.TargetPath,
                    Name = item.DisplayName,
                });
                config.Save(config.Current);
                refresh();
            });
        }

        if (item.Pinned is { Type: "folder" } folder)
        {
            Add(menu, "Menu_OpenFolder", () => OpenPath(folder.Path));
        }

        Add(menu, "Menu_ShowInExplorer", () => ShowInExplorer(item.TargetPath));
        menu.Items.Add(new Separator());

        if (item.Group is not null)
        {
            AppWindowGroup target = item.Group;
            Add(menu, "Menu_CloseAll", () => ConfirmCloseAll(target, launcher));
            Add(menu, "Menu_ExitApp", () => ConfirmCloseAll(target, launcher));
        }

        menu.Items.Add(new Separator());
        Add(menu, "Menu_DockSettings", openSettings);
        Add(menu, "Menu_HideDock", hideDock);
        return menu;
    }

    private static void Add(ContextMenu menu, string key, Action action)
    {
        var item = new MenuItem { Header = Loc.Instance.T(key) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static void ConfirmCloseAll(AppWindowGroup group, IAppLauncher launcher)
    {
        MessageBoxResult result = MessageBox.Show(
            Loc.Instance.T("Dialog_CloseAllText"),
            Loc.Instance.T("Dialog_CloseAllTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            launcher.CloseAll(group);
        }
    }

    private static void ShowInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (Directory.Exists(path))
        {
            OpenPath(path);
            return;
        }

        if (!File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true,
        });
    }

    /// <summary>用系统默认方式打开文件 / 文件夹（Jump List 项、文件夹固定项复用）。</summary>
    private static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 路径失效：忽略。配置保留、不自动删除（需求第 9 章约束）。
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
