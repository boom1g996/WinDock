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

/// <summary>单一动画时间轴；空闲时必须取消 Rendering 订阅（设计决策 D4）。</summary>
public interface IAnimationEngine : IDisposable
{
    event EventHandler<AnimationFrameEventArgs>? Frame;
    void Configure(double magnifyScale, double radius, int durationMs,
        double speedMultiplier, bool reducedMotion, string decay = "cosine");
    void SetHover(int? hoverIndex, int itemCount, double baseSize, double spacing);
    void Tap(int index);
}

/// <summary>启动、激活、关闭窗口（FR-301 / AC-04）。</summary>
public interface IAppLauncher
{
    bool Launch(PinnedItem item);
    bool NewWindow(PinnedItem item);
    bool Activate(AppWindowGroup group);
    bool Activate(AppWindowInfo window);
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

/// <summary>每帧动画输出：各图标缩放与水平位移补偿。</summary>
public sealed class AnimationFrameEventArgs : EventArgs
{
    public AnimationFrameEventArgs(double[] scales, double[] offsets, bool settled)
    {
        Scales = scales;
        Offsets = offsets;
        IsSettled = settled;
    }

    public double[] Scales { get; }
    public double[] Offsets { get; }
    public bool IsSettled { get; }
}
