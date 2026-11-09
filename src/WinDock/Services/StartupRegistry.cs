using System.IO;
using Microsoft.Win32;
using WinDock.Core.Interfaces;

namespace WinDock.Services;

/// <summary>开机自启：仅写 HKCU\...\Run（需求 4.4 允许的用户项，绝不写系统项）。</summary>
public static class StartupRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinDock";

    public static void Apply(bool enabled, ILogService log)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                log.Warn("Startup", "无法打开 HKCU Run 项，跳过自启设置");
                return;
            }

            if (enabled)
            {
                string executable = Environment.ProcessPath ?? "";
                if (executable.Length == 0)
                {
                    return;
                }

                key.SetValue(ValueName, $"\"{executable}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Error("Startup", "写入开机自启失败", ex);
        }
    }
}
