using System.Windows.Media;

namespace WinDock.Core.Models;

/// <summary>一个可枚举的顶层窗口。</summary>
public sealed class AppWindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public int ProcessId { get; init; }
    public string ExecutablePath { get; init; } = "";
    public string ClassName { get; init; } = "";
    public bool IsForeground { get; init; }
    public bool IsMinimized { get; init; }
}

/// <summary>按进程 / 可执行路径聚合后的应用分组，Dock 上一个图标对应一组。</summary>
public sealed class AppWindowGroup
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public List<AppWindowInfo> Windows { get; } = new();
    public bool IsPinned { get; set; }
    public DateTime LastActiveUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Dock 渲染用的视图模型：固定项、运行组、分隔符、空态按钮统一表示。</summary>
public sealed class DockItem
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public PinnedItem? Pinned { get; init; }
    public AppWindowGroup? Group { get; init; }
    public ImageSource? Icon { get; set; }
    public bool IsSeparator { get; init; }
    public bool IsAddButton { get; init; }

    /// <summary>系统区按钮（回收站 / 任务视图 / 显示桌面，WD-024）。</summary>
    public bool IsSystemItem { get; init; }

    /// <summary>系统项类型：recycleBin | taskView | showDesktop。</summary>
    public string SystemKind { get; init; } = "";

    public bool IsRunning => (Group?.Windows.Count ?? 0) > 0;
    public bool IsForeground => Group?.Windows.Any(w => w.IsForeground) == true;

    /// <summary>用于图标提取的目标路径。</summary>
    public string TargetPath => Pinned?.Path ?? Group?.ExecutablePath ?? "";

    /// <summary>
    /// 错误原因（10 UI 规范错误态）：路径失效 / 图标源过小等，非空时图标右上角显示红点，
    /// hover 的 ToolTip 给出原因。配置项本身保留，不自动删除（需求第 9 章约束）。
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>任务栏边缘位置（M0 实测本机为 Top，禁止硬编码 Bottom）。</summary>
public enum TaskbarEdge
{
    Unknown,
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>简化的矩形，避免 Core 依赖 System.Windows.Rect。</summary>
public readonly record struct RectBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>原生任务栏的只读快照，隐藏前保存，恢复时原样写回（设计决策 D2）。</summary>
public sealed class TaskbarSnapshot
{
    public IntPtr Handle { get; init; }
    public RectBounds Bounds { get; init; }
    public TaskbarEdge Edge { get; init; }
    public bool IsAutoHide { get; init; }
    public IReadOnlyList<IntPtr> SecondaryHandles { get; init; } = Array.Empty<IntPtr>();
    public bool IsValid => Handle != IntPtr.Zero && Bounds.Width > 0 && Bounds.Height > 0;
}
