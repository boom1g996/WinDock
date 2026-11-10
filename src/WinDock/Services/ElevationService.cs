using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using WinDock.Core.Interfaces;

namespace WinDock.Services;

/// <summary>
/// R5：管理员权限应用无法被普通权限的 Dock 操作（UIPI 限制，v1 不做绕过）。
/// 需求给出的对策是「UI 明示 + 提供以管理员重启 Dock 选项」，本类只负责后者。
///
/// 关键时序：单实例互斥由**当前进程**持有，直接 runas 拉起新实例会让它撞互斥后自杀，
/// 所以先由普通权限的 `--elevate` 子进程请求老实例优雅退出、等它释放互斥，
/// 再以 runas 启动。需求 8.6「不以管理员身份常驻」不变：这是用户显式触发的动作。
/// </summary>
public static class ElevationService
{
    private const string Module = "Elevation";

    /// <summary>以 runas 启动自身（DockHost.exe）。用户取消提权（1223）不算错误。</summary>
    public static bool StartSelfElevated(ILogService? log)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            log?.Warn(Module, "取不到自身可执行文件路径，无法提权重启");
            return false;
        }

        try
        {
            // UseShellExecute + runas 才会触发 UAC；WorkingDirectory 必须显式给，
            // 否则提权后工作目录会落到 System32，相对路径（日志/配置）全错。
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using Process? started = Process.Start(info);
            if (started is null)
            {
                log?.Warn(Module, "提权进程未启动");
                return false;
            }

            log?.Info(Module, $"已以管理员权限启动新实例 pid={started.Id}");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            log?.Info(Module, "用户取消了管理员授权，Dock 保持普通权限");
            return false;
        }
        catch (Exception ex)
        {
            log?.Error(Module, "提权重启失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 拉起 `--elevate` 引导进程（普通权限）。托盘菜单与设置窗口都用这一条路径，
    /// 因为真正的「先退后起」时序只有独立进程做得干净。
    /// </summary>
    public static bool RequestRestartElevated(ILogService? log)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            log?.Warn(Module, "取不到自身可执行文件路径，无法提权重启");
            return false;
        }

        try
        {
            var info = new ProcessStartInfo(exe)
            {
                Arguments = "--elevate",
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
                // 引导进程不建窗口，避免闪一下黑框。
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using Process? started = Process.Start(info);
            return started is not null;
        }
        catch (Exception ex)
        {
            log?.Error(Module, "拉起提权引导进程失败", ex);
            return false;
        }
    }

    /// <summary>等待除自己以外的 DockHost 全部退出（最多 <paramref name="timeout"/>）。</summary>
    public static bool WaitForOtherInstances(TimeSpan timeout)
    {
        int self = Environment.ProcessId;
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            bool any = false;
            foreach (Process process in Process.GetProcessesByName("DockHost"))
            {
                using (process)
                {
                    if (process.Id != self)
                    {
                        any = true;
                        break;
                    }
                }
            }

            if (!any)
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return false;
    }
}
