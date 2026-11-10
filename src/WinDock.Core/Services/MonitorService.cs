using System.Runtime.InteropServices;
using WinDock.Core.Native;

namespace WinDock.Core.Services;

/// <summary>DIP 坐标矩形（不依赖 System.Windows.Rect，保持 Core 无 WPF 依赖）。</summary>
public readonly record struct DipRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>单个显示器的物理信息与 DPI 缩放。</summary>
public sealed record DisplayMonitor(
    IntPtr Handle,
    int Left,
    int Top,
    int Right,
    int Bottom,
    int WorkLeft,
    int WorkTop,
    int WorkRight,
    int WorkBottom,
    int DpiX,
    int DpiY,
    bool IsPrimary)
{
    public double ScaleX => DpiX / 96.0;
    public double ScaleY => DpiY / 96.0;

    /// <summary>工作区换算为 DIP（WPF Left/Top 使用 1/96 英寸单位）。</summary>
    public DipRect WorkAreaDip => new(
        WorkLeft / ScaleX,
        WorkTop / ScaleY,
        (WorkRight - WorkLeft) / ScaleX,
        (WorkBottom - WorkTop) / ScaleY);
}

/// <summary>
/// 显示器枚举与 DPI 查询（WD-021 / 6.7）：用于把 Dock 定位到指定显示器、
/// 响应热插拔/分辨率变化后重新布局，并为「副屏可选开启」提供目标工作区。
/// 只读，不改变任何系统状态。
/// </summary>
public static class MonitorService
{
    /// <summary>枚举全部显示器（顺序由系统给出，主屏通常在前）。</summary>
    public static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        var list = new List<DisplayMonitor>(2);
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMonitor, _, _, _) =>
            {
                var info = new NativeMethods.MONITORINFO
                {
                    Size = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
                };
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    int dpiX = 96;
                    int dpiY = 96;
                    if (NativeMethods.GetDpiForMonitor(hMonitor, 0, out uint x, out uint y) == 0)
                    {
                        dpiX = (int)x;
                        dpiY = (int)y;
                    }

                    list.Add(new DisplayMonitor(
                        hMonitor,
                        info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom,
                        info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom,
                        dpiX, dpiY, (info.Flags & 1) != 0));
                }

                return true;
            }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// 按配置的 monitor id 取目标工作区（DIP）。
    /// id 支持 "primary"（默认）与 "1"/"2"…（按枚举序号，1 起）。
    /// 无法解析时返回主屏工作区，保证永远有合法落点。
    /// </summary>
    public static DipRect ResolveWorkArea(string? monitorId, out bool isPrimary)
    {
        isPrimary = true;
        IReadOnlyList<DisplayMonitor> monitors = GetMonitors();
        if (monitors.Count == 0)
        {
            return new DipRect(0, 0, 1920, 1080);
        }

        DisplayMonitor? target = null;
        if (!string.IsNullOrWhiteSpace(monitorId)
            && !string.Equals(monitorId, "primary", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(monitorId, out int index)
            && index >= 1 && index <= monitors.Count)
        {
            target = monitors[index - 1];
        }

        target ??= monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        isPrimary = target.IsPrimary;
        return target.WorkAreaDip;
    }
}
