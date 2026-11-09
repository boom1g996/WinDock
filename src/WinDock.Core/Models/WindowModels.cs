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
    public bool IsRunning => (Group?.Windows.Count ?? 0) > 0;
    public bool IsForeground => Group?.Windows.Any(w => w.IsForeground) == true;

    /// <summary>用于图标提取的目标路径。</summary>
    public string TargetPath => Pinned?.Path ?? Group?.ExecutablePath ?? "";
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
