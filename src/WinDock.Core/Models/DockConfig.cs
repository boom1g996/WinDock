namespace WinDock.Core.Models;

/// <summary>配置根对象，字段与需求文档第 9 章草案一致（schemaVersion = 1）。</summary>
/// <remarks>
/// 序列化统一用 camelCase；未知字段由 System.Text.Json 默认忽略（向前兼容）。
/// 默认值中的 taskbarMode=beautify 来自需求文档第 15 章已批复默认值「模式 C」，
/// 与草案示例的 replace 不同，属于刻意的产品决策。
/// M2 新增字段全部带默认值且 schemaVersion 保持 1（设计决策 D4），旧配置可直接读取。
/// </remarks>
public sealed class DockConfig
{
    public int SchemaVersion { get; set; } = 1;
    public GeneralConfig General { get; set; } = new();
    public DockLayoutConfig Dock { get; set; } = new();
    public AppearanceConfig Appearance { get; set; } = new();
    public AnimationConfig Animation { get; set; } = new();
    public BehaviorConfig Behavior { get; set; } = new();
    public List<PinnedItem> Pinned { get; set; } = new();

    /// <summary>返回出厂默认配置。</summary>
    public static DockConfig CreateDefault() => new();
}

/// <summary>通用行为。</summary>
public sealed class GeneralConfig
{
    public bool LaunchAtStartup { get; set; } = true;
    public string TaskbarMode { get; set; } = "beautify";
    public string Language { get; set; } = "zh-CN";

    /// <summary>首次启动引导是否已完成（WD-017）；旧配置缺字段时为 false，会弹一次引导。</summary>
    public bool FirstRunCompleted { get; set; }
}

/// <summary>Dock 布局参数（FR-101 / FR-102）。</summary>
public sealed class DockLayoutConfig
{
    public string Position { get; set; } = "bottom";
    public List<string> MonitorIds { get; set; } = new() { "primary" };
    public int IconSize { get; set; } = 48;
    public int IconSpacing { get; set; } = 10;
    public int Padding { get; set; } = 10;
    public int CornerRadius { get; set; } = 22;
    public string IconShape { get; set; } = "roundedRect";
    public int OffsetFromEdge { get; set; } = 8;
}

/// <summary>外观参数（6.10）。</summary>
public sealed class AppearanceConfig
{
    public string BackgroundMode { get; set; } = "acrylic";
    public double Opacity { get; set; } = 0.30;
    public string BlurLevel { get; set; } = "medium";
    public string BorderColor { get; set; } = "#1FFFFFFF";
    public double BorderWidth { get; set; } = 1;
    public string ShadowLevel { get; set; } = "medium";
    public ReflectionConfig Reflection { get; set; } = new();
    public string AccentSource { get; set; } = "system";
}

/// <summary>倒影参数（M1 仅落配置，不做渲染）。</summary>
public sealed class ReflectionConfig
{
    public bool Enabled { get; set; }
    public double Strength { get; set; } = 0.4;
    public int Height { get; set; } = 24;
}

/// <summary>动效参数（FR-201 / FR-202~204）。</summary>
public sealed class AnimationConfig
{
    public double MagnifyScale { get; set; } = 1.75;
    public double MagnifyRadius { get; set; } = 2.5;
    public int MagnifyDurationMs { get; set; } = 120;
    public string Decay { get; set; } = "cosine";
    public bool BounceEnabled { get; set; } = true;
    public double SpeedMultiplier { get; set; } = 1.0;
    public bool RespectReducedMotion { get; set; } = true;

    /// <summary>启动跳动次数（FR-202 默认 2 次，总时长约 600ms）。</summary>
    public int BounceCount { get; set; } = 2;

    /// <summary>注意提示跳动（FR-203）；M2 预研确认无可靠 WinEvent，默认关闭。</summary>
    public bool BounceAttentionEnabled { get; set; }

    /// <summary>运行指示点淡入淡出时长（FR-204）。</summary>
    public int IndicatorFadeMs { get; set; } = 150;
}

/// <summary>交互行为参数（6.10 / 6.6 / M2 新增）。</summary>
public sealed class BehaviorConfig
{
    public bool AutoHide { get; set; } = true;
    public int HideDelayMs { get; set; } = 800;
    public int EdgeHotzonePx { get; set; } = 2;
    public string ClickActiveApp { get; set; } = "showWindows";
    public string RunningSort { get; set; } = "recent";
    public bool ShowTrayIcon { get; set; } = true;
    public string HotkeyToggle { get; set; } = "Ctrl+Alt+D";

    /// <summary>缩略图预览总开关（FR-401）。</summary>
    public bool PreviewEnabled { get; set; } = true;

    /// <summary>悬停到弹出预览的延迟（FR-401，200~1500ms）。</summary>
    public int PreviewHoverDelayMs { get; set; } = 500;

    /// <summary>同时注册的 DWM 缩略图上限（FR-404，最多 6 个）。</summary>
    public int PreviewMaxWindows { get; set; } = 6;

    /// <summary>全局快捷键 Ctrl+Alt+D 开关；仅使用 RegisterHotKey，不装全局鼠标钩子。</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>全屏/演示模式时自动隐藏 Dock 并禁用热区（FR-603）。</summary>
    public bool FullScreenHide { get; set; } = true;

    /// <summary>看门狗开关（WD-020）。</summary>
    public bool WatchdogEnabled { get; set; } = true;

    /// <summary>看门狗心跳间隔，主进程失联超过该时长即恢复任务栏（AC-11 默认 10s）。</summary>
    public int WatchdogHeartbeatMs { get; set; } = 10000;

    /// <summary>图标磁盘缓存开关（FR-6.11 / WD-018）。</summary>
    public bool IconCacheEnabled { get; set; } = true;

    /// <summary>
    /// 本地性能打点开关（WD-033 / 8.6）：进程 CPU 采样与突发归因写入本地日志，
    /// 不做任何网络上报。默认开启，供 8.1 全量回归；对稳态开销 <0.1%。
    /// </summary>
    public bool MetricsEnabled { get; set; } = true;
}

/// <summary>固定项；type = app | uwp | separator。</summary>
public sealed class PinnedItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "app";
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public string Aumid { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>合法取值范围集中定义，夹紧逻辑只认这里，避免 UI 与配置层各写一份。</summary>
public static class ConfigRanges
{
    public const int MinSchemaVersion = 1;
    public static readonly (int Min, int Max) IconSize = (32, 96);
    public static readonly (int Min, int Max) IconSpacing = (0, 32);
    public static readonly (int Min, int Max) Padding = (4, 32);
    public static readonly (int Min, int Max) CornerRadius = (0, 50);
    public static readonly (int Min, int Max) OffsetFromEdge = (0, 64);
    public static readonly (double Min, double Max) Opacity = (0.0, 1.0);
    public static readonly (double Min, double Max) BorderWidth = (0.0, 4.0);
    public static readonly (double Min, double Max) MagnifyScale = (1.0, 2.5);
    public static readonly (double Min, double Max) MagnifyRadius = (1.0, 5.0);
    public static readonly (int Min, int Max) MagnifyDurationMs = (60, 300);
    public static readonly (double Min, double Max) SpeedMultiplier = (0.5, 2.0);
    public static readonly (int Min, int Max) HideDelayMs = (0, 5000);
    public static readonly (int Min, int Max) EdgeHotzonePx = (1, 20);
    public static readonly (double Min, double Max) ReflectionStrength = (0.0, 1.0);
    public static readonly (int Min, int Max) ReflectionHeight = (0, 64);

    // M2 新增（设计说明 D4：schemaVersion 保持 1，但新字段同样必须夹紧）
    public static readonly (int Min, int Max) PreviewHoverDelayMs = (200, 1500);
    public static readonly (int Min, int Max) PreviewMaxWindows = (1, 6);
    public static readonly (int Min, int Max) WatchdogHeartbeatMs = (1000, 60000);
    public static readonly (int Min, int Max) IndicatorFadeMs = (0, 600);
    public static readonly (int Min, int Max) BounceCount = (1, 3);

    public static readonly string[] TaskbarModes = { "replace", "coexist", "beautify" };
    public static readonly string[] Languages = { "zh-CN", "en" };
    public static readonly string[] Positions = { "bottom", "left", "right" };
    public static readonly string[] IconShapes = { "rect", "roundedRect", "circle" };
    public static readonly string[] BackgroundModes = { "none", "solid", "acrylic", "mica", "image" };
    public static readonly string[] BlurLevels = { "low", "medium", "high" };
    public static readonly string[] ShadowLevels = { "none", "weak", "medium", "strong", "custom" };
    public static readonly string[] DecayModes = { "cosine", "gaussian", "linear" };
    public static readonly string[] ClickActiveAppModes = { "minimize", "none", "showWindows" };
    public static readonly string[] RunningSorts = { "recent", "launchTime", "manual" };
}
