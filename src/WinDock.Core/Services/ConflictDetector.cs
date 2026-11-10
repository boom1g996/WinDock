using System.IO;
using WinDock.Core.Native;

namespace WinDock.Core.Services;

/// <summary>一条第三方任务栏美化工具冲突记录（工具名 + 判定依据，便于用户核对）。</summary>
public sealed record ConflictInfo(string Tool, string Evidence);

/// <summary>
/// R9：启动时检测与第三方任务栏美化工具（StartAllBack / StartIsBack /
/// ExplorerPatcher / RetroBar / TaskbarX）的冲突。
///
/// 判定只用「无副作用」的两类证据，绝不注入、不读内存：
/// 1. 卸载注册表项 DisplayName（HKCU / HKLM，含 32 位视图）——
///    这类工具都会登记卸载项，是最稳的"已安装"信号；
/// 2. 已知安装路径是否存在 —— 覆盖部分绿色版 / 便携版。
///
/// 检测到只做「提示」，不自动改用户设置：这些工具与 WinDock 的
/// 任务栏接管/注入存在真实冲突（需求 R9 影响等级「低」，对策是提示）。
/// </summary>
public static class ConflictDetector
{
    /// <summary>卸载项路径（64 位视图与 32 位视图分别读）。</summary>
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>已知会接管任务栏 / 注入 explorer 的美化工具及其识别关键字。</summary>
    private static readonly (string Tool, string[] Keywords)[] Known =
    {
        ("StartAllBack", new[] { "StartAllBack" }),
        ("StartIsBack", new[] { "StartIsBack" }),
        ("ExplorerPatcher", new[] { "ExplorerPatcher", "Explorer Patcher" }),
        ("RetroBar", new[] { "RetroBar" }),
        ("TaskbarX", new[] { "TaskbarX" }),
    };

    /// <summary>
    /// 执行检测。任何 IO/注册表异常一律吞掉并返回已收集结果 ——
    /// 检测失败绝不能影响 Dock 启动（R9 影响等级为「低」）。
    /// </summary>
    public static IReadOnlyList<ConflictInfo> Detect()
    {
        var found = new List<ConflictInfo>();

        try
        {
            ScanUninstall(found);
        }
        catch (Exception)
        {
            // 注册表不可读（受限账户/被安全软件拦截）时静默降级。
        }

        try
        {
            ScanPaths(found);
        }
        catch (Exception)
        {
        }

        return found;
    }

    /// <summary>把检测结果拼成一行可读文本（写日志与气泡用）。</summary>
    public static string Describe(IReadOnlyList<ConflictInfo> conflicts)
    {
        if (conflicts.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("; ", conflicts.Select(c => $"{c.Tool}({c.Evidence})"));
    }

    private static void ScanUninstall(List<ConflictInfo> found)
    {
        foreach (IntPtr root in new[] { NativeMethods.HKEY_CURRENT_USER, NativeMethods.HKEY_LOCAL_MACHINE })
        {
            foreach (string name in NativeMethods.SubKeyNames(root, UninstallKey))
            {
                string? displayName = NativeMethods.ReadString(root, UninstallKey + "\\" + name,
                    "DisplayName");
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                foreach ((string tool, string[] keywords) in Known)
                {
                    if (keywords.Any(k => displayName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        AddOnce(found, tool, "已安装：" + displayName.Trim());
                    }
                }
            }
        }
    }

    private static void ScanPaths(List<ConflictInfo> found)
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var probes = new (string Tool, string Path)[]
        {
            ("StartAllBack", Path.Combine(programFiles, "StartAllBack", "StartAllBackCfg.exe")),
            ("StartIsBack", Path.Combine(programFiles, "StartIsBack", "StartIsBackCfg.exe")),
            ("ExplorerPatcher", Path.Combine(appData, "ExplorerPatcher")),
            ("ExplorerPatcher", Path.Combine(localAppData, "ExplorerPatcher")),
            ("RetroBar", Path.Combine(localAppData, "Programs", "RetroBar")),
            ("TaskbarX", Path.Combine(localAppData, "TaskbarX")),
        };

        foreach ((string tool, string path) in probes)
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (Directory.Exists(path) || File.Exists(path))
            {
                AddOnce(found, tool, "存在安装目录：" + path);
            }
        }
    }

    private static void AddOnce(List<ConflictInfo> found, string tool, string evidence)
    {
        if (found.Any(c => string.Equals(c.Tool, tool, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        found.Add(new ConflictInfo(tool, evidence));
    }
}
