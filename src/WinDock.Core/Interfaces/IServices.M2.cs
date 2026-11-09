using System.Windows;
using System.Windows.Media;
using WinDock.Core.Models;

namespace WinDock.Core.Interfaces;

/// <summary>
/// M2 新增模块边界（设计说明 §3 / 需求 14.2）：实现放在 WinDock.Core.Services，
/// 由 App.xaml.cs 唯一组合根注入；模块之间只依赖这些接口，禁止直接引用实现类。
/// 本文件只定义契约，实现按 WD-014~020 分批落地。
/// </summary>

/// <summary>窗口缩略图预览目标（FR-402）。</summary>
public sealed record WindowPreviewTarget(IntPtr Handle, string Title, bool IsMinimized);

/// <summary>DWM 实时缩略图生命周期；面板关闭必须注销全部句柄（FR-404 / R7）。</summary>
public interface IPreviewService : IDisposable
{
    bool IsAvailable { get; }
    int MaxThumbnails { get; }
    /// <summary>在宿主窗口上注册缩略图；返回成功注册数量，超出上限的窗口被忽略。</summary>
    int Show(IntPtr host, IReadOnlyList<WindowPreviewTarget> targets);
    /// <summary>按面板布局更新每个缩略图的目标矩形（设备像素）。</summary>
    void UpdateLayout(IReadOnlyList<Rect> destinations);
    /// <summary>注销全部缩略图并隐藏面板。</summary>
    void Hide();
    event EventHandler? StateChanged;
}

/// <summary>自动隐藏状态机：热区、延迟、全屏抑制、滑入滑出（FR-601~603）。</summary>
public interface IAutoHideController : IDisposable
{
    bool IsHidden { get; }
    void ApplyConfig(DockConfig config);
    void Show();
    void Hide();
    event EventHandler<bool>? VisibilityChanged;
}

/// <summary>全屏检测：QUNS 状态 + 前台窗口是否覆盖整屏的双判据（FR-603）。</summary>
public interface IFullScreenDetector : IDisposable
{
    bool IsFullScreen { get; }
    void Start();
    void Stop();
    event EventHandler<bool>? StateChanged;
}

/// <summary>固定项拖拽重排与拖出取消固定；顺序写回 IConfigService（AC-06）。</summary>
public interface IDockDragService
{
    bool IsDragging { get; }
    void Reorder(int fromIndex, int toIndex);
    void Remove(int index);
    event EventHandler? OrderChanged;
}

/// <summary>首次启动 3 步引导（WD-017）；默认模式 C，不隐藏原生任务栏。</summary>
public interface IStartupGuide
{
    bool ShouldShow(DockConfig config);
    void Show(Action onCompleted);
}

/// <summary>图标磁盘缓存，key = 路径 + mtime + 尺寸（FR-6.11 / WD-018）。</summary>
public interface IIconCache
{
    ImageSource? Load(string key);
    void Store(string key, ImageSource icon);
    void Clear();
}

/// <summary>独立进程看门狗与心跳（AC-11 / WD-020）。</summary>
public interface IWatchdogService : IDisposable
{
    void Start();
    void Stop();
    void Heartbeat();
}

/// <summary>本地性能打点：冷启动、点击激活 P95、悬停首帧、帧率（8.1 / WD-033）。</summary>
public interface IMetricsService
{
    void Mark(string checkpoint);
    void Record(string metric, double milliseconds);
    IReadOnlyDictionary<string, double> Snapshot();
}

/// <summary>
/// 全局唯一的延时调度器（M2 设计决策 D3）：所有 500ms 悬停、800ms 隐藏、
/// 300ms 面板关闭共用一个 Timer，禁止各功能/图标起独立 DispatcherTimer。
/// </summary>
public interface ITimerScheduler : IDisposable
{
    void Schedule(string key, int delayMs, Action action);
    void Cancel(string key);
}
