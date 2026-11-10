using System.Windows.Media;
using WinDock.Core.Models;

namespace WinDock.Core.Interfaces;

/// <summary>本地日志（需求 14.7：格式、脱敏、保留 7 天）。</summary>
public interface ILogService
{
    void Debug(string module, string message);
    void Info(string module, string message);
    void Warn(string module, string message);
    void Error(string module, string message, Exception? exception = null);
}

/// <summary>配置读写与热更新（需求 6.14 / 9）。</summary>
public interface IConfigService : IDisposable
{
    DockConfig Current { get; }
    string ConfigPath { get; }
    event EventHandler<ConfigChangedEventArgs>? Changed;
    void Save(DockConfig config);
    void Reload();

    /// <summary>导出当前配置到指定路径（JSON，含 schemaVersion，WD-031 / 6.14）。</summary>
    void ExportTo(string path);

    /// <summary>
    /// 从指定路径导入配置：解析 + 夹紧 + 原子写，成功后通过 Changed 广播热更新。
    /// 解析失败抛 <see cref="System.IO.InvalidDataException"/>，调用方负责提示。
    /// </summary>
    DockConfig ImportFrom(string path);
}

/// <summary>原生任务栏隐藏 / 恢复；默认模式 C 下不得调用 Hide（设计决策 D1）。</summary>
public interface ITaskbarInterop : IDisposable
{
    TaskbarSnapshot Inspect();
    bool Hide();
    bool Restore();
    bool IsHidden { get; }
    bool HasPendingRestore { get; }
    uint TaskbarCreatedMessage { get; }
    event EventHandler? TaskbarRecreated;
    void NotifyTaskbarCreated();
}

/// <summary>事件驱动的窗口枚举（设计决策 D6）。</summary>
public interface IWindowEnumerator : IDisposable
{
    event EventHandler? WindowsChanged;
    IReadOnlyList<AppWindowGroup> Groups { get; }
    void Start();
    void Stop();
}

/// <summary>高清图标提取与内存缓存。</summary>
public interface IIconService
{
    ImageSource? GetIcon(string path, int size);
    ImageSource? GetIcon(PinnedItem item, int size);
    void Invalidate(string path);
}

/// <summary>
/// 单一动画时间轴；空闲时必须取消 Rendering 订阅（设计决策 D4 / M2 D3）。
/// M2 把放大、跳动、指示点淡入淡出、Dock 滑入滑出全部收敛到这一个时间轴，
/// 禁止任何图标/功能自持 DispatcherTimer 或独立渲染循环。
/// </summary>
public interface IAnimationEngine : IDisposable
{
    event EventHandler<AnimationFrameEventArgs>? Frame;

    /// <summary>是否正在渲染循环（自检与性能回归用；空闲必须为 false）。</summary>
    bool IsRunning { get; }

    /// <summary>当前 Dock 滑出位移（DIP）；0 表示完全显示。</summary>
    double SlideOffset { get; }

    void Configure(double magnifyScale, double radius, int durationMs,
        double speedMultiplier, bool reducedMotion, string decay = "cosine");

    /// <summary>M2 动效时长：指示点淡入淡出与滑入滑出（FR-204 / FR-601）。</summary>
    void ConfigureMotion(int indicatorFadeMs, int slideDurationMs);

    void SetHover(int? hoverIndex, int itemCount, double baseSize, double spacing);
    void Tap(int index);

    /// <summary>启动跳动（FR-202）；count 次阻尼跳动，总时长 300*count ms。</summary>
    void Bounce(int index, double iconHeight, int count);

    /// <summary>注意提示跳动（FR-203）：原地 200ms 小幅跳动一次。</summary>
    void Attention(int index, double iconHeight);

    /// <summary>Dock 侧最小化反馈（WD-032 / 6.9）：图标 300ms 向下「落入」Dock。</summary>
    void Drop(int index, double iconHeight);

    void StopBounce();

    /// <summary>指示点目标状态；数组长度变化视为结构重建，新项直接就位不淡入。</summary>
    void SetIndicatorStates(bool[] running, bool[] foreground);

    /// <summary>滑入滑出到指定位移（DIP）；0 = 显示，HiddenDistance = 隐藏。</summary>
    void Slide(double target);
}

/// <summary>启动、激活、关闭窗口（FR-301 / AC-04 / WD-037 AC-05 关闭按钮）。</summary>
public interface IAppLauncher
{
    bool Launch(PinnedItem item);
    bool NewWindow(PinnedItem item);
    bool Activate(AppWindowGroup group);
    bool Activate(AppWindowInfo window);

    /// <summary>向单个窗口发送 WM_CLOSE（异步；窗口可拒绝，如未保存提示）。返回是否投递成功。</summary>
    bool CloseWindow(AppWindowInfo window);

    /// <summary>批量关闭（走 <see cref="CloseWindow"/> 同一路径，避免两套 WM_CLOSE 逻辑）。</summary>
    void CloseAll(AppWindowGroup group);
}

/// <summary>配置变更事件参数，携带夹紧时产生的警告。</summary>
public sealed class ConfigChangedEventArgs : EventArgs
{
    public ConfigChangedEventArgs(DockConfig config, IReadOnlyList<string> warnings)
    {
        Config = config;
        Warnings = warnings;
    }

    public DockConfig Config { get; }
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>
/// 每帧动画输出：各图标缩放 / 水平位移 / 垂直跳动 / 指示点透明度，
/// 以及整条 Dock 的滑出位移。所有通道由同一时间轴计算（M2 D3）。
/// </summary>
public sealed class AnimationFrameEventArgs : EventArgs
{
    public AnimationFrameEventArgs(double[] scales, double[] offsets, bool settled,
        double[]? bounceOffsets = null, double[]? indicatorOpacity = null,
        double slideOffset = 0)
    {
        Scales = scales;
        Offsets = offsets;
        IsSettled = settled;
        BounceOffsets = bounceOffsets;
        IndicatorOpacity = indicatorOpacity;
        SlideOffset = slideOffset;
    }

    public double[] Scales { get; }
    public double[] Offsets { get; }
    public bool IsSettled { get; }
    public double[]? BounceOffsets { get; }
    public double[]? IndicatorOpacity { get; }
    public double SlideOffset { get; }
}
