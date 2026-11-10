using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinDock.Controls;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Native;
using WinDock.Core.Services;
using WinDock.Services;

namespace WinDock;

/// <summary>
/// Dock 主条（WD-006~009）：无边框胶囊、固定区/分隔符/运行区/空态、动画帧应用、
/// 点击与右键交互。只依赖接口，具体实现由 App.xaml.cs 组合根注入。
/// </summary>
public partial class MainWindow : Window, IDockWindowHost, IDockDragHost
{
    private const string Module = "Dock";
    private const string PreviewOpenKey = "preview.open";
    private const string PreviewCloseKey = "preview.close";
    private const string PreviewRefreshKey = "preview.close.refresh";
    private const string PreviewHoldReason = "preview";
    private const int PreviewCloseDelayMs = 300;

    /// <summary>安装器 / 冒烟脚本的优雅退出请求（App.xaml 为 OnExplicitShutdown）。</summary>
    private readonly uint _requestExitMessage =
        NativeMethods.RegisterWindowMessage("WinDock.RequestExit.v1");

    /// <summary>
    /// 自动化用的布局探针：收到消息后把每个图标的**屏幕坐标**写进日志。
    /// 冒烟脚本不再靠「窗口左边 + 62px」这类猜测定位图标——Dock 是
    /// SizeToContent + 内容居中，窗口宽度随条目数变化，猜测必然漂移。
    /// </summary>
    private readonly uint _dumpLayoutMessage =
        NativeMethods.RegisterWindowMessage("WinDock.DumpLayout.v1");

    private readonly IConfigService _config;
    private readonly ILogService _log;
    private readonly ITaskbarInterop _taskbar;
    private readonly IWindowEnumerator _enumerator;
    private readonly IIconService _icons;
    private readonly IAnimationEngine _animation;
    private readonly IAppLauncher _launcher;
    private readonly IMetricsService? _metrics;
    private readonly WinDock.Core.Services.FrameRateCounter _frameRate = new();
    private long _nextFpsLogTick;
    private readonly List<DockIconControl> _iconControls = new();
    private DockConfig _current;
    private string _lastStructure = "";

    // M2：自动隐藏宿主状态。_baseTop 是 Dock 完全显示时的目标 Y，
    // 滑动只改 _slideOffset，动画数值全部来自 IAnimationEngine 单一时间轴。
    private IAutoHideController? _autoHide;
    private IHotkeyService? _hotkey;
    private IPreviewService? _preview;
    private ITimerScheduler? _timers;
    private DockDragController? _drag;
    private DockConfig? _pendingConfig; // 拖拽期间到达的配置热更新，落点后再应用
    private PreviewWindow? _previewWindow;
    private string? _previewGroupKey;

    /// <summary>FR-304：由「点击已激活应用」打开的切换面板为常驻面板，鼠标移开不自动关闭。</summary>
    private bool _previewPinned;

    /// <summary>behavior.runningSort=manual 时的首次出现顺序（进程存活期间稳定）。</summary>
    private readonly Dictionary<string, int> _manualOrder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>behavior.runningSort=launchTime 用的进程启动时间缓存（避免每帧查进程）。</summary>
    private readonly Dictionary<string, DateTime> _launchTimes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>FR-203：ShellHook 消息与事件号（应用请求注意时 shell 广播 HSHELL_FLASH）。</summary>
    private readonly uint _shellHookMessage = NativeMethods.RegisterWindowMessage("SHELLHOOK");
    private bool _shellHookRegistered;
    private double _baseTop;
    private double _baseLeft;
    private double _slideOffset;
    private bool _pointerOver;
    private bool _animationsPaused;

    // WD-032：最小化事件钩子（映射到对应图标触发「落入 Dock」反馈）。
    private IntPtr _minimizeHook;
    private NativeMethods.WinEventProc? _minimizeProc;

    // 6.10 材质组 / FR-101：当前实际生效的材质分支与强调色（供图标控件复用）。
    private readonly string? _monitorOverride;
    private Color _accentColor = Color.FromRgb(0x0A, 0x84, 0xFF);
    private string _materialMode = "acrylic";
    private double _tintAlpha = 0.3;

    /// <summary>质感：胶囊背景的自绘毛玻璃（抓屏 + 高斯模糊，模糊只落在胶囊形状内）。</summary>
    private readonly GlassBackgroundService _glass;

    /// <summary>玻璃刷新的去抖计时器：滑入/放大/换屏会连续改窗口位置，稳定后再抓屏。</summary>
    private DispatcherTimer? _glassDebounce;

    /// <summary>磨砂颗粒刷（64×64 平铺），只生成一次，进程内复用。</summary>
    private Brush? _noiseBrush;

    /// <param name="monitorOverride">
    /// 6.7 多显示器：副屏 Dock 传该屏的 monitor id（"1"/"2"…）；
    /// null 表示跟随配置 Dock.MonitorIds[0]。
    /// </param>
    public MainWindow(IConfigService config, ILogService log, ITaskbarInterop taskbar,
        IWindowEnumerator enumerator, IIconService icons, IAnimationEngine animation,
        IAppLauncher launcher, IMetricsService? metrics = null, string? monitorOverride = null)
    {
        _monitorOverride = monitorOverride;
        InitializeComponent();
        _config = config;
        _log = log;
        _taskbar = taskbar;
        _enumerator = enumerator;
        _icons = icons;
        _animation = animation;
        _launcher = launcher;
        _metrics = metrics;
        _current = config.Current;
        _glass = new GlassBackgroundService(log);

        _animation.Frame += OnAnimationFrame;
        Root.MouseEnter += OnRootMouseEnter;
        Root.MouseLeave += OnRootMouseLeave;
        // WD-030：允许从资源管理器拖入 .exe/.lnk/文件夹添加固定项（与内部重排互不冲突）。
        Root.AllowDrop = true;
        Root.DragOver += OnExternalDragOver;
        Root.Drop += OnExternalDrop;
        // 拖拽手势：按下来自图标，移动/抬起统一由 Root 的捕获处理（WD-016）。
        Root.PreviewMouseMove += (_, args) => _drag?.OnPreviewMouseMove(args);
        Root.PreviewMouseLeftButtonUp += (_, args) => _drag?.OnPreviewMouseUp(args);
        KeyDown += OnWindowKeyDown;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        // 倒影需要按图标区实际尺寸重建 Viewbox，尺寸变化时同步（FR-101）。
        // 这里挂的是布局回调，异常会直接冒到 Dispatcher → 整窗崩溃（19:20 实机教训），
        // 因此兜底吞掉并记日志：倒影是装饰性功能，绝不能拖垮主条。
        Capsule.SizeChanged += (_, _) =>
        {
            try
            {
                UpdateReflection(_current.Appearance.Reflection);
            }
            catch (Exception ex)
            {
                _log.Warn(Module, "倒影重建失败（已跳过本次尺寸变化）：" + ex.Message);
            }

            // 胶囊尺寸变了，玻璃的抓取矩形也过期了（去抖，滑入动画期间不会抓）。
            ScheduleGlassRefresh();
        };

        ApplyConfig(_current);
    }

    /// <summary>
    /// 副屏 Dock 的退出路径：先注销预览缩略图再关窗，避免共享 IPreviewService 里留下悬空句柄。
    /// </summary>
    public void CloseForShutdown()
    {
        ClosePreview();
        _animation.Frame -= OnAnimationFrame;
        Close();
    }

    /// <summary>Dock 是否被用户手动隐藏（区别于窗口 Visibility）。</summary>
    public bool IsDockVisible { get; private set; } = true;

    /// <summary>全屏/演示模式下暂停图标动效，但保留滑入滑出（FR-603 / D7）。</summary>
    public bool AnimationsPaused
    {
        get => _animationsPaused;
        set
        {
            if (_animationsPaused == value)
            {
                return;
            }

            _animationsPaused = value;
            if (value)
            {
                // 全屏时缩略图会盖在独占应用上；立即注销 DWM 句柄（D7 / R7）。
                ClosePreview();
            }
        }
    }

    /// <summary>组合根在窗口显示前绑定自动隐藏与快捷键，避免可见性状态竞态。</summary>
    public void BindAutoHide(IAutoHideController autoHide, IHotkeyService hotkey)
    {
        _autoHide = autoHide;
        _hotkey = hotkey;
        autoHide.ApplyConfig(_current);
    }

    /// <summary>组合根注入 WD-014 预览服务与唯一延时调度器（M2 D3）。</summary>
    public void BindPreview(IPreviewService preview, ITimerScheduler timers)
    {
        _preview = preview;
        _timers = timers;
        preview.ApplyConfig(_current);
    }

    /// <summary>组合根注入 WD-016 拖拽服务；控制器复用同一个延时调度器（不新增 Timer）。</summary>
    public void BindDrag(IDockDragService drag, ITimerScheduler timers)
    {
        _drag = new DockDragController(this, drag, timers, _log);
        drag.OrderChanged += (_, _) =>
        {
            _lastStructure = ""; // 顺序变化必须重建可视化树
            RefreshItems();
        };
    }

    // ---- IDockDragHost（显式实现，避免与 XAML 生成的 Root/IconsPanel 字段重名） ----

    Grid IDockDragHost.Root => Root;
    Panel IDockDragHost.IconsPanel => IconsPanel;
    UIElement IDockDragHost.HitArea => Capsule;
    DockConfig IDockDragHost.Config => _current;
    void IDockDragHost.CancelPreview() => ClosePreview();
    void IDockDragHost.ResetHover() => ClearHover();

    void IDockDragHost.RefreshAfterDrop()
    {
        DockConfig? pending = _pendingConfig;
        _pendingConfig = null;
        if (pending is not null)
        {
            ApplyConfig(pending);
            return;
        }

        _lastStructure = "";
        RefreshItems();
    }

    public void ShowDock()
    {
        IsDockVisible = true;
        if (_autoHide is not null)
        {
            _autoHide.Show();
        }
        else
        {
            Visibility = Visibility.Visible;
        }
    }

    public void HideDock()
    {
        IsDockVisible = false;
        ClosePreview();
        ClearHover();
        if (_autoHide is not null)
        {
            _autoHide.Hide(force: true);
        }
        else
        {
            Visibility = Visibility.Collapsed;
        }
    }

    // ---- IDockWindowHost -------------------------------------------------

    public event EventHandler? PointerEntered;
    public event EventHandler? PointerLeft;

    /// <summary>WD-022：左/右位置时 Dock 垂直排列，隐藏方向改为水平滑出。</summary>
    private bool IsVertical => _current.Dock.Position is "left" or "right";

    public double HiddenDistance => (IsVertical
        ? (ActualWidth > 0 ? ActualWidth : 126)
        : (ActualHeight > 0 ? ActualHeight : 126)) + 8;

    public bool IsPointerOverDock => _pointerOver;

    public void SetSlideOffset(double dip)
    {
        if (Math.Abs(_slideOffset - dip) < 0.01)
        {
            return;
        }

        _slideOffset = dip;
        if (IsVertical)
        {
            // 左侧 Dock 向屏幕外滑出 = Left 减小；右侧 Dock = Left 增大。
            double sign = _current.Dock.Position == "left" ? -1 : 1;
            Left = _baseLeft + (sign * dip);
        }
        else
        {
            Top = _baseTop + dip;
        }

        // K-23：滑入/滑出动画靠 CompositionTarget.Rendering 推进，而改 Top/Left 只移动
        // 窗口、不产生内容重绘——光标停在热区不动（无后续鼠标消息）时渲染管线静默，
        // 实测滑入冻结在 12px 处（Dock 只露 10px peek）。这里每帧强制失效一次视觉，
        // 保证动画活跃期间合成器持续出帧；静止时本方法不会被调用。
        InvalidateVisual();
    }

    public void SetVisible(bool visible)
    {
        IsDockVisible = visible;
        if (!visible)
        {
            ClosePreview();
        }

        // K-22 记录：本窗口 SizeToContent="WidthAndContent"，任何 Visibility 非 Visible
        // （Collapsed 与 Hidden 都一样）都会让 WPF 把 HWND 收缩到 2×2，唤出时重新测量布局。
        // 已实测 Hidden 无法避免，故维持 Collapsed；测试脚本按「窗口身份」而非尺寸识别。
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 按当前工作区重算 Left / _baseTop。折叠态（2×2）残留的错位尺寸会在可见后
    /// 被复用，导致唤回后 Dock 出现在错误位置（用户在「自动收起后无法再唤起」中
    /// 反馈的体感之一），因此 Show 路径必须重定位。
    /// </summary>
    public void Reposition()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Position));
            return;
        }

        if (Visibility == Visibility.Visible)
        {
            Position();
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Position));
        }
    }

    public void SetInteractive(bool interactive) => IsHitTestVisible = interactive;

    /// <summary>设置窗口打开期间抑制自动隐藏，保证 Dock 常驻可见（AC-09 实时预览）。</summary>
    public void SetSettingsHeld(bool held) => _autoHide?.SetHeld("settings", held);

    private void OnRootMouseEnter(object sender, MouseEventArgs e)
    {
        _pointerOver = true;
        _timers?.Cancel(PreviewCloseKey); // 从预览面板移回 Dock：取消关闭倒计时
        PointerEntered?.Invoke(this, EventArgs.Empty);

        // 玻璃自愈：锁屏 / 安全桌面 / 全屏独占期间抓屏会被跳过，那时停在兜底纯色。
        // 用户一碰 Dock 就重试一次（缓存命中时会直接复用，不会重复抓屏）。
        ScheduleGlassRefresh();
    }

    private void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        _pointerOver = false;
        // 图标放大/水平位移会让命中边界瞬时缩回，产生 MouseLeave→MouseEnter 抖动。
        // 延后一帧复核：若此时鼠标仍在 Root 内就不清空 hover，避免动画目标反复横跳。
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (Root.IsMouseOver)
            {
                _pointerOver = true;
                return;
            }

            ClearHover();
            if (_previewWindow?.IsOpen == true)
            {
                // 面板 Show 会改变 Z 序并可能瞬时触发一次 MouseLeave。
                // 延后做一次「联合区域复核」，而不是直接关面板，否则会形成
                // 打开→关闭→再打开的 ~1s 循环（实机冒烟曾把空闲 CPU 推到 27%）。
                _timers?.Schedule(PreviewCloseKey, PreviewCloseDelayMs, VerifyPreviewLeave);
            }

            PointerLeft?.Invoke(this, EventArgs.Empty);
        }));
    }

    /// <summary>配置热更新入口：外观、布局、动画参数全部即时生效（AC-09）。</summary>
    public void ApplyConfig(DockConfig config)
    {
        if (_drag?.IsDragging == true)
        {
            // 拖拽中重建会销毁源控件、清空手势状态；记录待应用配置，落点后补做。
            _pendingConfig = config;
            return;
        }

        _drag?.Cancel(); // 非拖拽态的热更新：确保没有残留手势
        _current = config;

        // 6.10 材质组：背景模式 / 模糊强度 / 边框 / 阴影 / 倒影 / 强调色统一在此应用。
        ApplyMaterial(config.Appearance, config.Dock);

        double grow = config.Dock.IconSize * (config.Animation.MagnifyScale - 1);
        bool vertical = IsVertical;
        IconsPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        IconsPanel.VerticalAlignment = vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        Capsule.VerticalAlignment = vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        Capsule.HorizontalAlignment = config.Dock.Position switch
        {
            "left" => HorizontalAlignment.Left,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        // 垂直 Dock 放大沿水平方向生长，需要左右留白；底部 Dock 向上生长，顶部留白。
        Root.Margin = vertical
            ? new Thickness(grow + 14, grow / 2 + 10, grow + 14, grow / 2 + 10)
            : new Thickness(grow / 2 + 10, grow + 12, grow / 2 + 10, 0);

        bool reduced = config.Animation.RespectReducedMotion
                       && NativeMethods.IsReducedMotionEnabled();
        _animation.Configure(
            config.Animation.MagnifyScale,
            config.Animation.MagnifyRadius,
            config.Animation.MagnifyDurationMs,
            config.Animation.SpeedMultiplier,
            reduced,
            config.Animation.Decay);
        _animation.ConfigureMotion(config.Animation.IndicatorFadeMs, 180);

        _preview?.ApplyConfig(config);
        if (!config.Behavior.PreviewEnabled)
        {
            ClosePreview(); // 设置窗口关掉预览：立即注销句柄，不等下一次悬停
        }

        // 外观（尺寸 / 间距 / 形状）变化走 RebuildIcons：结构未变时只就地刷新已有图标，
        // 不强制清空重建——否则配置热重载风暴会把整条 Dock 反复清空并卡死主线程。
        RebuildIcons();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Position));
        ApplyBackdrop();
    }

    /// <summary>窗口枚举事件 / 配置变更后的重建入口（UI 线程安全）。</summary>
    public void RefreshItems()
    {
        if (_drag?.IsDragging == true)
        {
            return; // 拖拽期间窗口枚举变化不重建；RefreshAfterDrop 会补一次
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(RefreshItems);
            return;
        }

        if (RebuildIcons())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Position));
        }
    }

    /// <returns>true 表示全量重建（需要重新定位），false 表示只做了增量绑定。</returns>
    private bool RebuildIcons()
    {
        List<DockItem> items = BuildItems();
        string structure = string.Join("|", items.Select(item =>
            $"{(item.IsSeparator ? "s" : item.IsAddButton ? "a" : "i")}:{item.Key}"));
        if (string.Equals(structure, _lastStructure, StringComparison.Ordinal))
        {
            // 结构未变：仅原地刷新绑定与外观（尺寸 / 形状），不重建、不闪烁。
            UpdateIcons(items);
            ApplyIconConfigs(_current);
            SyncIndicators();
            return false;
        }

        _lastStructure = structure;
        RenderIcons(items);
        SyncIndicators();
        return true;
    }

    /// <summary>结构未变时，对已有图标控件就地应用外观配置（尺寸 / 形状），避免整条 Dock 重建闪烁。</summary>
    private void ApplyIconConfigs(DockConfig config)
    {
        foreach (DockIconControl control in _iconControls)
        {
            control.ApplyConfig(config);
        }
    }

    /// <summary>把指示点目标状态同步给动画引擎；结构重建时新项直接就位，不集体淡入。</summary>
    private void SyncIndicators()
    {
        var running = new bool[_iconControls.Count];
        var foreground = new bool[_iconControls.Count];
        for (int i = 0; i < _iconControls.Count; i++)
        {
            running[i] = _iconControls[i].IsRunning;
            foreground[i] = _iconControls[i].IsForeground;
        }

        _animation.SetIndicatorStates(running, foreground);
    }

    /// <summary>只组装数据，不触碰可视化树；窗口标题变化也能在这里刷新到右键菜单。</summary>
    private List<DockItem> BuildItems()
    {
        DockConfig config = _current;
        int iconPx = RequestedIconPx();
        IReadOnlyList<AppWindowGroup> groups = _enumerator.Groups;
        var usedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<DockItem>();

        foreach (PinnedItem pinned in config.Pinned)
        {
            if (pinned.Type == "separator")
            {
                items.Add(new DockItem { IsSeparator = true, Key = pinned.Id });
                continue;
            }

            AppWindowGroup? group = groups.FirstOrDefault(g =>
                !string.IsNullOrEmpty(pinned.Path)
                && string.Equals(g.ExecutablePath, pinned.Path, StringComparison.OrdinalIgnoreCase));
            if (group is not null)
            {
                usedGroups.Add(group.Key);
            }

            string name = !string.IsNullOrWhiteSpace(pinned.Name)
                ? pinned.Name
                : group?.DisplayName ?? Path.GetFileNameWithoutExtension(pinned.Path);
            ImageSource? icon = _icons.GetIcon(pinned, iconPx)
                                ?? (pinned.Type == "folder" ? CreateSystemGlyph("folder") : null);
            items.Add(new DockItem
            {
                Key = pinned.Id,
                DisplayName = name,
                Pinned = pinned,
                Group = group,
                Icon = icon,
                // 10 UI 规范错误态：路径失效 / 图标取不到时打红点，配置项保留不删除。
                Error = ResolveItemError(pinned, icon) ?? ResolveLowResIcon(icon),
            });
        }

        List<AppWindowGroup> running = SortRunning(
            groups.Where(g => !usedGroups.Contains(g.Key)).ToList(), config.Behavior.RunningSort);
        if (items.Count > 0 && running.Count > 0 && !items[^1].IsSeparator)
        {
            items.Add(new DockItem { IsSeparator = true, Key = "runtime-separator" });
        }

        foreach (AppWindowGroup group in running)
        {
            ImageSource? runningIcon = _icons.GetIcon(group.ExecutablePath, iconPx);
            items.Add(new DockItem
            {
                Key = group.Key,
                DisplayName = group.DisplayName,
                Group = group,
                Icon = runningIcon,
                Error = ResolveLowResIcon(runningIcon),
            });
        }

        AppendSystemArea(items);

        if (items.Count == 0)
        {
            items.Add(new DockItem
            {
                IsAddButton = true,
                Key = "add",
                DisplayName = Loc.Instance.T("Dock_AddApp"),
            });
        }

        return items;
    }

    /// <summary>
    /// FR-304 窗口切换面板：点击「已激活应用」时把该应用所有窗口的缩略图展开并**常驻**，
    /// 再次点击同一图标收起（等同 macOS「点已激活应用显示所有窗口」）。
    /// </summary>
    private void OpenWindowSwitcher(DockIconControl control, AppWindowGroup group)
    {
        if (_previewWindow is { IsOpen: true } && _previewPinned
            && string.Equals(_previewGroupKey, group.Key, StringComparison.Ordinal))
        {
            ClosePreview();
            return;
        }

        ClosePreview();
        if (!_current.Behavior.PreviewEnabled)
        {
            // 预览被关闭时退化为窗口列表菜单（保留可用性，不静默失败）。
            OpenMenu(control);
            return;
        }

        OpenPreview(control, group, pinned: true);
        if (_previewWindow is not { IsOpen: true })
        {
            OpenMenu(control); // 面板打不开（无可用窗口）时同样退化为菜单
        }
    }

    /// <summary>运行区排序（6.10 行为组）：recent 最近使用 / launchTime 启动时间 / manual 首次出现顺序。</summary>
    private List<AppWindowGroup> SortRunning(List<AppWindowGroup> running, string sort)
    {
        for (int i = 0; i < running.Count; i++)
        {
            _manualOrder.TryAdd(running[i].Key, _manualOrder.Count);
        }

        switch (sort)
        {
            case "launchTime":
                return running
                    .OrderBy(g => ResolveLaunchTime(g), Comparer<DateTime>.Default)
                    .ToList();
            case "manual":
                return running
                    .OrderBy(g => _manualOrder.TryGetValue(g.Key, out int order) ? order : int.MaxValue)
                    .ToList();
            default:
                return running.OrderByDescending(g => g.LastActiveUtc).ToList();
        }
    }

    private DateTime ResolveLaunchTime(AppWindowGroup group)
    {
        if (_launchTimes.TryGetValue(group.Key, out DateTime cached))
        {
            return cached;
        }

        DateTime value = DateTime.MaxValue;
        try
        {
            int pid = group.Windows.FirstOrDefault()?.ProcessId ?? 0;
            if (pid > 0)
            {
                value = System.Diagnostics.Process.GetProcessById(pid).StartTime;
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        _launchTimes[group.Key] = value;
        return value;
    }

    /// <summary>
    /// 10 UI 规范错误态：固定项路径失效或图标提取失败时给出原因（图标上角红点 + ToolTip）。
    /// 需求第 9 章明确「路径失效时图标显示问号并保留配置，不自动删除」。
    /// </summary>
    private string? ResolveItemError(PinnedItem pinned, ImageSource? icon)
    {
        if (pinned.Type == "folder" && !string.IsNullOrWhiteSpace(pinned.Path)
            && !Directory.Exists(pinned.Path))
        {
            return Loc.Instance.T("Error_PathMissing");
        }

        if (pinned.Type == "app" && !string.IsNullOrWhiteSpace(pinned.Path)
            && !File.Exists(pinned.Path))
        {
            return Loc.Instance.T("Error_PathMissing");
        }

        return icon is null && pinned.Type != "separator"
            ? Loc.Instance.T("Error_IconMissing")
            : null;
    }

    /// <summary>
    /// 6.11：源图标小于 48px 时不能放大糊掉，标记出来并在 hover 提示用户
    /// （需求原文「小于 48px 的源图标需标记并提示」）。
    /// </summary>
    private string? ResolveLowResIcon(ImageSource? icon)
    {
        if (icon is not System.Windows.Media.Imaging.BitmapSource bitmap
            || bitmap.PixelWidth <= 0 || bitmap.PixelWidth >= 48)
        {
            return null;
        }

        return Loc.Instance.T("Error_IconLowRes", bitmap.PixelWidth);
    }

    /// <summary>WD-024：在运行区之后追加系统区（回收站 / 任务视图 / 显示桌面），可单独开关。</summary>
    private void AppendSystemArea(List<DockItem> items)
    {
        BehaviorConfig b = _current.Behavior;
        if (!b.ShowSystemArea)
        {
            return;
        }

        var sysItems = new List<DockItem>(3);
        if (b.ShowRecycleBin)
        {
            sysItems.Add(SystemItem("recycleBin", "Dock_RecycleBin"));
        }

        if (b.ShowTaskView)
        {
            sysItems.Add(SystemItem("taskView", "Dock_TaskView"));
        }

        if (b.ShowShowDesktop)
        {
            sysItems.Add(SystemItem("showDesktop", "Dock_ShowDesktop"));
        }

        if (sysItems.Count == 0)
        {
            return;
        }

        if (items.Count > 0 && !items[^1].IsSeparator)
        {
            items.Add(new DockItem { IsSeparator = true, Key = "system-separator" });
        }

        items.AddRange(sysItems);
    }

    private DockItem SystemItem(string kind, string locKey) => new()
    {
        Key = $"sys:{kind}",
        DisplayName = Loc.Instance.T(locKey),
        IsSystemItem = true,
        SystemKind = kind,
        Icon = CreateSystemGlyph(kind),
    };

    /// <summary>
    /// 系统区图标用矢量字形渲染（回收站 / 任务视图 / 显示桌面 / 文件夹兜底），
    /// 避免依赖特定 exe 的图标提取，且在高 DPI 下始终清晰。
    /// </summary>
    private static ImageSource CreateSystemGlyph(string kind)
    {
        string data = kind switch
        {
            "recycleBin" => "M6,7 L18,7 L16.5,22 L7.5,22 Z M9,7 L9,3.5 L15,3.5 L15,7 Z",
            "taskView" => "M3,5 L13,5 L13,14 L3,14 Z M9,10 L21,10 L21,19 L9,19 Z",
            "showDesktop" => "M3,5 L21,5 L21,17 L3,17 Z M9,19 L15,19 L15,21 L9,21 Z",
            "folder" => "M3,6 L10,6 L12,8 L21,8 L21,19 L3,19 Z",
            _ => "M4,4 L20,4 L20,20 L4,20 Z",
        };

        var geometry = (PathGeometry)Geometry.Parse(data);
        geometry.FillRule = FillRule.Nonzero; // 重叠子路径实心填充，避免出现镂空
        var drawing = new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)), null, geometry);
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// 图标提取目标尺寸（**物理像素**）：放大态图标在屏幕上占
    /// <c>IconSize × MagnifyScale × DPI</c>，位图必须不小于这个数才不会二次拉伸。
    /// </summary>
    /// <remarks>
    /// 之前漏乘 DPI 是「图标糊」的主因之一：125% 缩放下 48px 图标实际占 60 物理像素，
    /// 放大态要 105，而位图只按 92px 提取再拉伸。另外返回值吸附到系统图标档位
    /// （32/48/64/128/256）—— <c>PrivateExtractIcons</c> 收到任意尺寸时也是取最接近
    /// 的一档再缩放，直接要整档更清晰，也能减少缓存档位数量。
    /// </remarks>
    private int RequestedIconPx()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double scale = Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
        if (scale <= 0)
        {
            scale = 1.0;
        }

        double need = _current.Dock.IconSize * _current.Animation.MagnifyScale * scale + 8;
        int px = (int)Math.Ceiling(need);
        return px switch
        {
            <= 32 => 32,
            <= 48 => 48,
            <= 64 => 64,
            <= 128 => 128,
            _ => 256,
        };
    }

    /// <summary>结构未变：原地更新绑定与指示点，不销毁控件、不打断 hover。</summary>
    private void UpdateIcons(List<DockItem> items)
    {
        int index = 0;
        foreach (DockItem item in items)
        {
            if (item.IsSeparator || item.IsAddButton)
            {
                continue;
            }

            if (index < _iconControls.Count)
            {
                _iconControls[index].Bind(item);
            }

            index++;
        }
    }

    private void RenderIcons(List<DockItem> items)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _drag?.Cancel(); // 结构重建会销毁拖拽源控件
        ClosePreview(); // 结构重建会销毁悬停的图标控件，先注销缩略图
        IconsPanel.Children.Clear();
        _iconControls.Clear();
        DockConfig config = _current;
        bool vertical = IsVertical;

        foreach (DockItem item in items)
        {
            if (item.IsSeparator)
            {
                IconsPanel.Children.Add(new Border
                {
                    Width = vertical ? config.Dock.IconSize * 0.6 : 1,
                    Height = vertical ? 1 : config.Dock.IconSize * 0.6,
                    Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                    Margin = vertical ? new Thickness(0, 8, 0, 8) : new Thickness(8, 0, 8, 0),
                    HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                    VerticalAlignment = vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom,
                });
                continue;
            }

            if (item.IsAddButton)
            {
                var add = new Button
                {
                    Content = "+",
                    Width = config.Dock.IconSize,
                    Height = config.Dock.IconSize,
                    FontSize = config.Dock.IconSize * 0.5,
                    Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    ToolTip = Loc.Instance.T("Dock_AddApp"),
                };
                add.Click += OnAddAppClick;
                IconsPanel.Children.Add(add);
                continue;
            }

            var control = new DockIconControl(item);
            control.ApplyConfig(config);
            control.Margin = vertical
                ? new Thickness(0, config.Dock.IconSpacing / 2.0, 0, config.Dock.IconSpacing / 2.0)
                : new Thickness(config.Dock.IconSpacing / 2.0, 0,
                    config.Dock.IconSpacing / 2.0, 0);
            control.MouseEnter += (_, _) => OnIconEnter(control);
            control.MouseLeave += (_, _) => control.SetHoverVisual(false);
            control.PreviewMouseLeftButtonDown += (_, args) =>
                _drag?.OnPreviewMouseDown(control, args);
            control.PreviewMouseUp += (_, args) => OnIconMouseUp(control, args);
            control.KeyDown += OnIconKeyDown;
            IconsPanel.Children.Add(control);
            _iconControls.Add(control);
        }

        ClearHover();
        _metrics?.Record("ui.rebuild_icons", sw.Elapsed.TotalMilliseconds);
    }

    private void OnIconEnter(DockIconControl control)
    {
        if (AnimationsPaused)
        {
            return; // 独占全屏 3D：暂停所有图标动效（FR-603）
        }

        control.SetHoverVisual(true); // FR-205 上移 2px + 4% 亮度
        int index = _iconControls.IndexOf(control);
        _animation.SetHover(index, _iconControls.Count,
            _current.Dock.IconSize, _current.Dock.IconSpacing);
        SchedulePreview(control);
    }

    private void ClearHover()
    {
        _animation.SetHover(null, _iconControls.Count,
            _current.Dock.IconSize, _current.Dock.IconSpacing);
    }

    // ---- WD-014 缩略图预览（FR-401~404） --------------------------------

    /// <summary>悬停后延迟打开；延时只走唯一 ITimerScheduler，不新增 Timer（D3）。</summary>
    private void SchedulePreview(DockIconControl control)
    {
        if (_preview is null || _timers is null)
        {
            return;
        }

        _timers.Cancel(PreviewCloseKey);
        _timers.Cancel(PreviewOpenKey);
        if (!_current.Behavior.PreviewEnabled
            || control.Item.Group is not { Windows.Count: > 0 } group)
        {
            if (_previewWindow?.IsOpen == true)
            {
                _timers.Schedule(PreviewCloseKey, PreviewCloseDelayMs, ClosePreview);
            }

            return;
        }

        int delay = Math.Clamp(_current.Behavior.PreviewHoverDelayMs,
            ConfigRanges.PreviewHoverDelayMs.Min, ConfigRanges.PreviewHoverDelayMs.Max);
        _timers.Schedule(PreviewOpenKey, delay, () => OpenPreview(control, group));
    }

    private void OpenPreview(DockIconControl control, AppWindowGroup group, bool pinned = false)
    {
        if (_preview is null || _timers is null || !IsDockVisible
            || !_iconControls.Contains(control) || AnimationsPaused
            || !_current.Behavior.PreviewEnabled)
        {
            return;
        }

        if (_previewWindow is { IsOpen: true }
            && string.Equals(_previewGroupKey, group.Key, StringComparison.Ordinal))
        {
            // 同一分组的重复 MouseEnter（图标放大抖动）不重建面板，只取消关闭倒计时。
            _timers.Cancel(PreviewCloseKey);
            return;
        }

        List<WindowPreviewTarget> targets = group.Windows
            .Take(_preview.MaxThumbnails)
            .Select(w => new WindowPreviewTarget(w.Handle, w.Title, w.IsMinimized))
            .ToList();
        int hiddenWindows = Math.Max(0, group.Windows.Count - targets.Count); // FR-402 超出显示 +N
        if (targets.Count == 0)
        {
            return;
        }

        if (_previewWindow is not null)
        {
            // 切换图标：先注销上一组句柄；WPF Window 关闭后不可复用，直接换新实例。
            _previewWindow.ClosePanel();
            _previewWindow = null;
        }

        _autoHide?.SetHeld(PreviewHoldReason, true);

        var panel = new PreviewWindow(_preview, _launcher, _log);
        panel.HoverEntered += (_, _) => _timers.Cancel(PreviewCloseKey);
        panel.HoverLeft += (_, _) =>
            _timers.Schedule(PreviewCloseKey, PreviewCloseDelayMs, VerifyPreviewLeave);
        panel.ThumbnailActivated += (_, _) => ClosePreview();
        // WD-037 / AC-05：单窗口关闭后刷新运行指示。WM_CLOSE 是异步请求，
        // 延时 600ms 等目标窗口退出（枚举器的 WindowsChanged 也会自行触发，
        // RefreshItems 幂等，两边重叠无害）。
        panel.WindowClosed += (_, _) =>
            _timers.Schedule(PreviewRefreshKey, 600, RefreshItems);
        _previewWindow = panel;
        _previewGroupKey = group.Key;
        _previewPinned = pinned;

        Point physical = control.PointToScreen(new Point(control.ActualWidth / 2, 0));
        Matrix fromDevice = PresentationSource.FromVisual(control)?
            .CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        panel.Open(control.Item.DisplayName, targets, fromDevice.Transform(physical), hiddenWindows);
    }

    /// <summary>
    /// 预览面板的离开判定：只有鼠标同时离开 Dock 与面板才关闭。
    /// 单次/双层复核，且**不重新调度自己**，避免「打开→关闭→再打开」循环
    /// （M2 第三批实机冒烟：该循环曾把空闲 CPU 推到 27%）。
    /// </summary>
    private void VerifyPreviewLeave()
    {
        if (_previewWindow is not { IsOpen: true } panel)
        {
            return;
        }

        if (_previewPinned || Root.IsMouseOver || panel.IsPointerOver)
        {
            return; // FR-304 切换面板常驻，只有再次点击图标 / Esc / 激活窗口才收起
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!ReferenceEquals(_previewWindow, panel) || !panel.IsOpen
                || Root.IsMouseOver || panel.IsPointerOver)
            {
                return;
            }

            ClosePreview();
        }));
    }

    /// <summary>幂等关闭：注销全部 DWM 句柄并解除自动隐藏抑制。</summary>
    private void ClosePreview()
    {
        _timers?.Cancel(PreviewOpenKey);
        _timers?.Cancel(PreviewCloseKey);
        PreviewWindow? panel = _previewWindow;
        _previewWindow = null;
        _previewGroupKey = null;
        _previewPinned = false;
        if (panel is not null)
        {
            panel.ClosePanel();
        }
        else
        {
            _preview?.Hide();
        }

        _autoHide?.SetHeld(PreviewHoldReason, false);
    }

    private void OnAnimationFrame(object? sender, AnimationFrameEventArgs e)
    {
        _metrics?.Count("ui.animation_frame");
        // WD-038 / AC-21：帧率统计只吃渲染回调（动画活跃才有帧，空闲无指标，
        // 与「悬停 60s」口径一致）；不另起 Timer，顺带在帧回调里做 5s 节流。
        long tick = Environment.TickCount64;
        _frameRate.AddFrame(tick);
        if (tick - _nextFpsLogTick >= 5000)
        {
            _nextFpsLogTick = tick;
            var fps = _frameRate.Snapshot();
            _log.Info("Metrics",
                $"fps 1s={fps.Last1s:F1} window60s avg={fps.WindowAvg:F1} " +
                $"p5={fps.WindowP5:F1} min={fps.WindowMin:F1} " +
                $"active bursts={fps.Bursts} avg={fps.ActiveAvg:F1} " +
                $"p5={fps.ActiveP5:F1} min={fps.ActiveMin:F1} " +
                $"dropped={fps.Dropped} frames={fps.Frames}");

        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnAnimationFrame(sender, e));
            return;
        }

        // 滑入滑出通道始终生效（全屏强制隐藏也依赖它），其余通道在全屏时暂停。
        SetSlideOffset(e.SlideOffset);
        // 拖拽期间源图标的 1.1 倍跟随由 DockDragController 独占，动画帧不得覆盖。
        if (AnimationsPaused || _drag?.IsDragging == true)
        {
            return;
        }

        int count = Math.Min(e.Scales.Length, _iconControls.Count);
        for (int i = 0; i < count; i++)
        {
            double bounce = e.BounceOffsets is not null && i < e.BounceOffsets.Length
                ? e.BounceOffsets[i]
                : 0;
            _iconControls[i].SetFrame(e.Scales[i], e.Offsets[i], bounce);
            if (e.IndicatorOpacity is not null && i < e.IndicatorOpacity.Length)
            {
                _iconControls[i].SetIndicatorFrame(e.IndicatorOpacity[i]);
            }
        }
    }

    private void OnIconMouseUp(DockIconControl control, MouseButtonEventArgs e)
    {
        if (_drag?.SuppressClick == true)
        {
            // 通用 PreviewMouseUp 可能早于控制器的 PreviewMouseLeftButtonUp，
            // 这里先行拦截，避免拖放结束顺带启动/激活应用。
            e.Handled = true;
            return;
        }

        bool controlModifier = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.ChangedButton == MouseButton.Right
            || (e.ChangedButton == MouseButton.Left && controlModifier))
        {
            OpenMenu(control);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            if (control.Item.Group is { } group && ConfirmCloseAll(group))
            {
                _launcher.CloseAll(group);
            }

            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _animation.Tap(_iconControls.IndexOf(control));
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (control.Item.Pinned is not null)
            {
                _launcher.NewWindow(control.Item.Pinned);
            }

            return;
        }

        HandlePrimaryClick(control);
    }

    private void HandlePrimaryClick(DockIconControl control)
    {
        DockItem item = control.Item;
        if (item.IsSystemItem)
        {
            HandleSystemItem(item);
            return;
        }

        // WD-030 / FR-303：固定的文件夹点击直接打开资源管理器。
        if (item.Pinned is { Type: "folder" } folder)
        {
            OpenFolder(folder.Path);
            return;
        }

        if (item.Group is { Windows.Count: > 0 } group)
        {
            bool foreground = group.Windows.Any(w => w.IsForeground);
            if (!foreground)
            {
                if (!_launcher.Activate(group))
                {
                    _log.Warn(Module, Loc.Instance.T("Error_ActivateFailed"));
                }

                return;
            }

            switch (_current.Behavior.ClickActiveApp)
            {
                case "minimize":
                    foreach (AppWindowInfo window in group.Windows)
                    {
                        NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_MINIMIZE);
                    }

                    break;
                case "none":
                    break;
                default:
                    OpenWindowSwitcher(control, group); // FR-304 窗口切换面板
                    break;
            }

            return;
        }

        if (item.Pinned is null)
        {
            return;
        }

        // FR-202 / AC-03：点击未运行的应用，图标阻尼跳动 2 次（600ms）后启动。
        // 启动失败立即停止，避免 600ms 无意义动画；窗口创建成功后由指示点淡入接棒。
        if (_current.Animation.BounceEnabled)
        {
            _animation.Bounce(_iconControls.IndexOf(control),
                _current.Dock.IconSize, _current.Animation.BounceCount);
        }

        if (!_launcher.Launch(item.Pinned))
        {
            _animation.StopBounce();
            _log.Warn(Module, Loc.Instance.T("Error_LaunchFailed"));
        }
    }

    private void OpenMenu(DockIconControl control)
    {
        control.ContextMenu = control.Item.IsSystemItem
            ? BuildSystemMenu(control.Item)
            : DockMenuBuilder.Build(
                control.Item, _config, _log, _launcher, RefreshItems,
                () => (Application.Current as App)?.ShowSettings(), HideDock);
        control.ContextMenu.PlacementTarget = control;
        control.ContextMenu.IsOpen = true;
    }

    /// <summary>WD-024：系统区按钮点击行为（回收站打开、任务视图、显示桌面）。</summary>
    private void HandleSystemItem(DockItem item)
    {
        switch (item.SystemKind)
        {
            case "recycleBin":
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    _log.Error(Module, "打开回收站失败", ex);
                }

                break;
            case "taskView":
                NativeMethods.SendWinKey(NativeMethods.VK_TAB);
                break;
            case "showDesktop":
                NativeMethods.SendWinKey(NativeMethods.VK_D);
                break;
        }
    }

    private ContextMenu BuildSystemMenu(DockItem item)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = item.DisplayName };
        open.Click += (_, _) => HandleSystemItem(item);
        menu.Items.Add(open);

        if (item.SystemKind == "recycleBin")
        {
            menu.Items.Add(new Separator());
            var empty = new MenuItem { Header = Loc.Instance.T("Menu_EmptyRecycleBin") };
            empty.Click += (_, _) => EmptyRecycleBinWithConfirm();
            menu.Items.Add(empty);
        }

        return menu;
    }

    private void EmptyRecycleBinWithConfirm()
    {
        MessageBoxResult result = MessageBox.Show(
            Loc.Instance.T("Dialog_EmptyRecycleBinText"),
            Loc.Instance.T("Dialog_EmptyRecycleBinTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        int hr = NativeMethods.EmptyRecycleBin();
        if (hr != 0)
        {
            _log.Warn(Module, $"清空回收站失败 HRESULT=0x{hr:X8}");
            return;
        }

        _lastStructure = "";
        RefreshItems();
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                _log.Warn(Module, Loc.Instance.T("Error_FolderNotFound"));
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error(Module, "打开文件夹失败", ex);
        }
    }

    /// <summary>WD-030：资源管理器拖入的视觉反馈（仅接受文件/文件夹拖放）。</summary>
    private void OnExternalDragOver(object sender, DragEventArgs e)
    {
        if (_current.Behavior.DragDropAddEnabled
            && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    /// <summary>
    /// WD-030 / FR-303：接受 .exe / .lnk / 文件夹拖入并添加为固定项；
    /// 已固定同一路径时跳过，避免重复项。
    /// </summary>
    private void OnExternalDrop(object sender, DragEventArgs e)
    {
        if (!_current.Behavior.DragDropAddEnabled
            || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        bool added = false;
        foreach (string path in paths)
        {
            bool isDir = Directory.Exists(path);
            bool isFile = File.Exists(path);
            if (!isDir && !isFile)
            {
                continue;
            }

            if (isFile)
            {
                string ext = Path.GetExtension(path);
                if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 只接受可执行/快捷方式/文件夹
                }
            }

            bool exists = _current.Pinned.Any(p =>
                string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                continue;
            }

            _current.Pinned.Add(new PinnedItem
            {
                Type = isDir ? "folder" : "app",
                Path = path,
                Name = Path.GetFileNameWithoutExtension(path),
            });
            added = true;
            _log.Info(Module, $"拖入添加固定项：{path}");
        }

        if (!added)
        {
            return;
        }

        _config.Save(_current);
        _lastStructure = "";
        RefreshItems();
    }

    private static bool ConfirmCloseAll(AppWindowGroup group)
    {
        _ = group;
        return MessageBox.Show(
            Loc.Instance.T("Dialog_CloseAllText"),
            Loc.Instance.T("Dialog_CloseAllTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void OnIconKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not DockIconControl control)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            HandlePrimaryClick(control);
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right)
        {
            int index = _iconControls.IndexOf(control);
            int next = Math.Clamp(index + (e.Key == Key.Right ? 1 : -1),
                0, Math.Max(0, _iconControls.Count - 1));
            if (next >= 0 && next < _iconControls.Count)
            {
                _iconControls[next].Focus();
            }

            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_drag?.IsDragging == true)
        {
            _drag.Cancel();
            e.Handled = true;
            return;
        }

        foreach (DockIconControl control in _iconControls)
        {
            if (control.ContextMenu?.IsOpen == true)
            {
                control.ContextMenu.IsOpen = false;
            }
        }

        if (_previewWindow?.IsOpen == true)
        {
            ClosePreview(); // FR-304 常驻面板用 Esc 收起
            e.Handled = true;
        }
    }

    private void OnAddAppClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Instance.T("Dialog_AddAppTitle"),
            Filter = "应用程序 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _config.Current.Pinned.Add(new PinnedItem
        {
            Type = "app",
            Path = dialog.FileName,
            Name = Path.GetFileNameWithoutExtension(dialog.FileName),
        });
        _config.Save(_config.Current);
        RefreshItems();
    }

    private void Position()
    {
        // 默认主屏用 SystemParameters.WorkArea（按 DIP 且自动排除原生任务栏）；
        // WD-021：配置了非主屏（MonitorIds[0] = "1"/"2"…）时改用该显示器工作区。
        Rect work = SystemParameters.WorkArea;
        string? monitorId = _monitorOverride ?? _current.Dock.MonitorIds?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(monitorId)
            && !string.Equals(monitorId, "primary", StringComparison.OrdinalIgnoreCase))
        {
            WinDock.Core.Services.DipRect dip =
                WinDock.Core.Services.MonitorService.ResolveWorkArea(monitorId, out bool isPrimary);
            if (!isPrimary)
            {
                work = new Rect(dip.Left, dip.Top, dip.Width, dip.Height);
            }
        }

        string position = _current.Dock.Position;
        if (position is "left" or "right")
        {
            // WD-022：垂直居中、贴左/贴右；滑入滑出沿水平方向。
            _baseLeft = position == "left"
                ? work.Left + _current.Dock.OffsetFromEdge
                : work.Right - ActualWidth - _current.Dock.OffsetFromEdge;
            _baseTop = work.Top + ((work.Height - ActualHeight) / 2.0);
            double sign = position == "left" ? -1 : 1;
            Left = _baseLeft + (sign * _slideOffset);
            Top = _baseTop;
        }
        else
        {
            _baseLeft = work.Left + ((work.Width - ActualWidth) / 2.0);
            _baseTop = work.Bottom - ActualHeight - _current.Dock.OffsetFromEdge;
            Left = _baseLeft;
            Top = _baseTop + _slideOffset;
        }

        // 窗口位置变了，胶囊相对屏幕的物理矩形也变了 → 玻璃要重抓。
        // 滑入动画每帧都会走到这里，靠去抖收敛成"停下后抓一次"。
        ScheduleGlassRefresh();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        HwndSource? source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);

        // 快捷键需要真实 HWND；ApplyConfig 先设定开关，Attach 再实际注册。
        _hotkey?.ApplyConfig(_current);
        _hotkey?.Attach(handle);

        // WD-032：监听最小化事件，在 Dock 侧播放「落入 Dock」反馈（不劫持系统动画）。
        if (_current.Behavior.MinimizeFeedbackEnabled && _minimizeHook == IntPtr.Zero)
        {
            _minimizeProc = OnMinimizeEvent;
            _minimizeHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
                NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
                IntPtr.Zero, _minimizeProc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        }

        // FR-203：注册 ShellHook —— 应用调用 FlashWindow 请求注意时 shell 会广播
        // HSHELL_FLASH（wParam），这是唯一可靠的非注入式「请求注意」信号。
        if (_current.Behavior.AttentionBounceEnabled && !_shellHookRegistered)
        {
            _shellHookRegistered = NativeMethods.RegisterShellHookWindow(handle);
            if (_shellHookRegistered)
            {
                _log.Info(Module, "已注册 ShellHook（注意提示跳动 FR-203 生效）");
            }
        }

        Closed += (_, _) =>
        {
            _glassDebounce?.Stop();
            _glassDebounce = null;

            if (_minimizeHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_minimizeHook);
                _minimizeHook = IntPtr.Zero;
                _minimizeProc = null;
            }

            if (_shellHookRegistered)
            {
                NativeMethods.DeregisterShellHookWindow(handle);
                _shellHookRegistered = false;
            }
        };
    }

    /// <summary>最小化事件：找到该窗口所属图标，播放向下「落入 Dock」反馈（WD-032）。</summary>
    private void OnMinimizeEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || !_current.Behavior.MinimizeFeedbackEnabled || AnimationsPaused)
        {
            return;
        }

        // 钩子在非 UI 线程回调；派发到 UI 线程再读取控件集合。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            for (int i = 0; i < _iconControls.Count; i++)
            {
                AppWindowGroup? group = _iconControls[i].Item.Group;
                if (group is not null && group.Windows.Any(w => w.Handle == hwnd))
                {
                    _animation.Drop(i, _current.Dock.IconSize);
                    break;
                }
            }
        }));
    }

    /// <summary>
    /// FR-203 注意提示跳动：应用调用 FlashWindowEx 请求注意时，在 Dock 侧对应图标
    /// 原地小幅跳动一次（200ms）。与最小化反馈共用 AnimationEngine 的跳动通道。
    /// </summary>
    private void OnAttentionFlash(IntPtr hwnd)
    {
        if (!_current.Behavior.AttentionBounceEnabled || AnimationsPaused || hwnd == IntPtr.Zero)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            for (int i = 0; i < _iconControls.Count; i++)
            {
                AppWindowGroup? group = _iconControls[i].Item.Group;
                if (group is not null && group.Windows.Any(w => w.Handle == hwnd))
                {
                    _animation.Attention(i, _current.Dock.IconSize);
                    break;
                }
            }
        }));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == unchecked((int)_taskbar.TaskbarCreatedMessage))
        {
            _taskbar.NotifyTaskbarCreated();
        }
        else if (msg == unchecked((int)NativeMethods.WM_HOTKEY))
        {
            _autoHide?.Toggle();
            handled = true;
        }
        else if (msg == unchecked((int)_requestExitMessage))
        {
            // 延后到消息处理结束再退出，保证 OnExit 的看门狗 S 握手正常执行。
            Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
            handled = true;
        }
        else if (msg == unchecked((int)_dumpLayoutMessage))
        {
            DumpIconLayout(wParam.ToInt64());
            handled = true;
        }
        else if (_shellHookRegistered && msg == unchecked((int)_shellHookMessage)
                 && wParam.ToInt32() == NativeMethods.HSHELL_FLASH)
        {
            // FR-203：lParam 为请求注意的窗口句柄。
            OnAttentionFlash(lParam);
        }
        else if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            // WD-021：分辨率 / 缩放 / 显示器热插拔后重新定位，避免卡在已断开的屏幕。
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Reposition));
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 把当前图标布局写入日志，供自动化定位（不改变任何运行时状态）。
    /// 输出形如：`layout nonce=1731 win=(l,t,w,h) dip=(l,t) hwnd=(l,t,r,b) count=3 0:Name@(x,y) ...`
    /// nonce 由调用方透传，自动化只认自己那一次请求的行。
    ///
    /// 图标中心用 **Win32 窗口矩形 + 控件相对窗口的布局偏移** 计算，而不是
    /// `Visual.PointToScreen`：本机实测 `PointToScreen` 对这个小窗口返回的原点
    /// 比 `GetWindowRect` 偏 (164, 247)，而鼠标命中判定走的是 HWND 真实矩形，
    /// 用 PointToScreen 定位会全部落空（2026-11-10 实机验证）。
    /// </summary>
    private void DumpIconLayout(long nonce)
    {
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            NativeMethods.GetWindowRect(handle, out NativeMethods.RECT rect);
            Point origin = PointToScreen(new Point(0, 0));

            var parts = new List<string>(_iconControls.Count);
            for (int i = 0; i < _iconControls.Count; i++)
            {
                DockIconControl control = _iconControls[i];
                if (!control.IsLoaded || control.ActualWidth <= 0)
                {
                    continue;
                }

                Point local = control.TransformToAncestor(this)
                    .Transform(new Point(control.ActualWidth / 2, control.ActualHeight / 2));
                int cx = rect.Left + (int)Math.Round(local.X * dpi.DpiScaleX);
                int cy = rect.Top + (int)Math.Round(local.Y * dpi.DpiScaleY);
                parts.Add($"{i}:{control.Item.DisplayName}@({cx},{cy})");
            }

            _log.Info("Layout",
                $"layout nonce={nonce} win=({(int)origin.X},{(int)origin.Y}," +
                $"{(int)ActualWidth},{(int)ActualHeight}) dip=({Left:F0},{Top:F0}) " +
                $"hwnd=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) count={_iconControls.Count} " +
                string.Join(' ', parts));
        }
        catch (Exception ex)
        {
            _log.Warn("Layout", $"导出图标布局失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 6.10 材质组总入口：把「背景模式 / 不透明度 / 模糊强度 / 边框 / 阴影 / 倒影 / 强调色」
    /// 一次性应用到胶囊上。设计原则：**窗口始终保持 DWM 玻璃（ApplyBackdrop）**，
    /// 差异全部落在 WPF 胶囊的填色上——这样任何模式下胶囊圆角外侧都能透出桌面，
    /// 不会出现「非分层窗口画不出透明背景 → 周围一圈黑底」的问题。
    /// </summary>
    private void ApplyMaterial(AppearanceConfig a, DockLayoutConfig d)
    {
        _accentColor = ResolveAccentColor(a);
        bool dwm = NativeMethods.IsDwmCompositionEnabled();
        bool remote = NativeMethods.IsRemoteSession();
        bool highContrast = SystemParameters.HighContrast;

        // 8.3 兼容性矩阵：DWM 不可用（虚拟机）/ 远程桌面 / 高对比度 → 强制纯色回退。
        string mode = a.BackgroundMode;
        string fallback = "";
        if (!dwm)
        {
            mode = "solid";
            fallback = "DWM 合成不可用";
        }
        else if (remote && mode is "acrylic" or "mica" or "image")
        {
            mode = "solid";
            fallback = "远程桌面会话";
        }
        else if (highContrast)
        {
            mode = "solid";
            fallback = "高对比度模式";
        }

        // R2 性能模式：材质固定纯色，不再叠 DWM 玻璃的模糊层。
        // 注意：不能把窗口背景也改成实色 —— 非分层窗口切不回透明，
        // 胶囊圆角外侧会出现一圈实色方块，因此玻璃仍保留给"窗口透明"用。
        if (a.PerformanceMode)
        {
            mode = "solid";
            fallback = fallback.Length > 0 ? fallback : "性能模式";
        }

        _materialMode = mode;
        double opacity = Math.Clamp(a.Opacity, 0, 1);
        // Windows 的 DWM 玻璃强度没有公开调节项（8.3/需求 4.2），
        // 「模糊强度」现在真正作用于高斯半径（见 GlassBlurRadius），这里只算 tint 浓度。
        double blurFactor = a.BlurLevel switch
        {
            "low" => 0.85,
            "high" => 1.20,
            _ => 1.0,
        };
        // opacity 语义 = 玻璃浓度：0 仍保 18% 玻璃底（全透明就不叫玻璃了，
        // 会退化成"图标浮在桌面上"，这正是用户反馈"看不到毛玻璃"的直接原因），
        // 1 对应约 64%，覆盖 macOS Dock 的观感区间。旧配置里出现过度 opacity=0。
        _tintAlpha = Math.Clamp(0.18 + (opacity * 0.46 * blurFactor), 0.18, 0.78);

        if (fallback.Length > 0)
        {
            _log.Info(Module, $"材质回退纯色：{fallback}（背景模式 {a.BackgroundMode} → solid）");
        }

        bool opaque = string.Equals(mode, "solid", StringComparison.Ordinal);
        if (highContrast)
        {
            // 8.3：高对比度下用系统色板并保证边框对比度（不叠任何半透明）。
            Capsule.Background = SystemColors.WindowBrush;
            Capsule.BorderBrush = SystemColors.WindowTextBrush;
            Capsule.BorderThickness = new Thickness(Math.Max(1, a.BorderWidth));
            TintLayer.Background = Brushes.Transparent;
            NoiseLayer.Opacity = 0;
        }
        else if (opaque)
        {
            Capsule.Background = new SolidColorBrush(Color.FromArgb(255, 0x1C, 0x1C, 0x1E));
            Capsule.BorderBrush = ParseBorderBrush(a.BorderColor);
            Capsule.BorderThickness = new Thickness(a.BorderWidth);
            TintLayer.Background = Brushes.Transparent;
            NoiseLayer.Opacity = 0;
        }
        else
        {
            // 毛玻璃：先铺一层"厚 tint"兜底 —— 抓屏失败（全屏独占 / UAC 安全桌面 /
            // 旧系统不支持截屏排除）时会停在这里，视觉上仍是可用的半透明深色胶囊。
            // 抓屏成功后 UpdateGlassBackground 把它换成「模糊位图 + 薄 tint」。
            Capsule.Background = new SolidColorBrush(
                ResolveCapsuleTint(Math.Clamp(_tintAlpha + 0.22, 0.30, 0.92)));
            Capsule.BorderBrush = ParseBorderBrush(a.BorderColor);
            Capsule.BorderThickness = new Thickness(a.BorderWidth);
            TintLayer.Background = Brushes.Transparent;
            NoiseLayer.Background = NoiseBrush();
            NoiseLayer.Opacity = 0.06;
        }

        Capsule.CornerRadius = new CornerRadius(d.CornerRadius);
        // 内层 tint / 噪点比外层圆角小一个描边宽度：叠在一起时才不会在四角
        // 露出 1px 的直角色块（Border 只按自己的圆角裁背景）。
        var innerRadius = new CornerRadius(Math.Max(0, d.CornerRadius - a.BorderWidth));
        TintLayer.CornerRadius = innerRadius;
        NoiseLayer.CornerRadius = innerRadius;
        Capsule.Padding = new Thickness(
            d.Padding, d.Padding, d.Padding, d.Padding + 8);

        // 6.10 阴影：无 / 弱 / 中 / 强（custom 按"强"处理，强度由倒影与描边微调）。
        Capsule.Effect = CreateShadowEffect(a.ShadowLevel);

        // FR-101 底部 1px 高光：深色背景上更亮，高对比度下关闭以免干扰。
        Highlight.Background = highContrast
            ? Brushes.Transparent
            : new LinearGradientBrush(
                Color.FromArgb(0, 255, 255, 255),
                Color.FromArgb(90, 255, 255, 255),
                new Point(0, 0), new Point(1, 0));
        Highlight.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;

        // 8.3 虚拟机 / 远程桌面下不画倒影（需求明确要求）；R2 性能模式一律关闭。
        bool reflectionAllowed = !a.PerformanceMode && !highContrast && !remote
            && !string.Equals(mode, "solid", StringComparison.Ordinal);
        UpdateReflection(reflectionAllowed ? a.Reflection : new ReflectionConfig());

        ApplyIconAccent();
        ApplyBackdrop();
        // 材质/尺寸/位置都可能变了，玻璃需要重抓（去抖，避免布局期反复抓屏）。
        ScheduleGlassRefresh(force: true);
    }

    /// <summary>
    /// 胶囊的 tint 色：深色主题近黑、浅色主题近白（macOS Dock 在浅色下是半透明白）。
    /// </summary>
    /// <remarks>
    /// 只读 HKCU 的 <c>SystemUsesLightTheme</c>，不写、不触发设置变更广播。
    /// 读取失败一律按深色处理（默认主题）。
    /// </remarks>
    private static Color ResolveCapsuleTint(double alpha)
    {
        byte a = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        bool light;
        try
        {
            light = NativeMethods.ReadDword(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme") == 1;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or ArgumentException or InvalidOperationException)
        {
            light = false;
        }

        return light
            ? Color.FromArgb(a, 0xF2, 0xF5, 0xFA)
            : Color.FromArgb(a, 0x0E, 0x10, 0x16);
    }

    /// <summary>
    /// 磨砂颗粒：64×64 随机灰度、低 alpha，平铺在胶囊上给玻璃一点"纸面颗粒"。
    /// 大面积半透明如果不加颗粒，看起来会像"一块塑料板"而不是磨砂玻璃。
    /// 只生成一次并冻结，进程内复用。
    /// </summary>
    private Brush NoiseBrush()
    {
        if (_noiseBrush is not null)
        {
            return _noiseBrush;
        }

        const int size = 64;
        int stride = size * 4;
        var pixels = new byte[stride * size];
        var random = new Random(20261110); // 固定种子：每次运行颗粒一致，避免"闪烁感"
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte v = (byte)random.Next(0, 256);
            pixels[i] = v;     // B
            pixels[i + 1] = v; // G
            pixels[i + 2] = v; // R
            pixels[i + 3] = (byte)random.Next(20, 64);
        }

        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();

        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, size, size),
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        _noiseBrush = brush;
        return brush;
    }

    /// <summary>模糊半径（物理像素）：模糊强度档位真正作用在这里，不再只影响底色浓度。</summary>
    private double GlassBlurRadius() => _current.Appearance.BlurLevel switch
    {
        "low" => 12,
        "high" => 34,
        _ => 22,
    };

    private static System.Windows.Media.Effects.DropShadowEffect? CreateShadowEffect(string level)
    {
        (double blur, double depth, double opacity) = level switch
        {
            "none" => (0, 0, 0),
            "weak" => (10, 2, 0.25),
            "strong" => (28, 10, 0.5),
            "custom" => (32, 12, 0.55),
            _ => (24, 8, 0.28), // medium（需求 10：0 8 24 rgba(0,0,0,.28)）
        };

        if (blur <= 0)
        {
            return null;
        }

        var effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = blur,
            ShadowDepth = depth,
            Opacity = opacity,
            Color = Colors.Black,
            Direction = 270, // 向下投影
        };
        effect.Freeze();
        return effect;
    }

    /// <summary>强调色：AccentSource=system 取系统主题色，否则用配置里的 #AARRGGBB。</summary>
    private Color ResolveAccentColor(AppearanceConfig a)
    {
        if (!string.Equals(a.AccentSource, "system", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(a.AccentColor))
        {
            try
            {
                Color parsed = (Color)ColorConverter.ConvertFromString(a.AccentColor)!;
                return Color.FromRgb(parsed.R, parsed.G, parsed.B);
            }
            catch (FormatException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        uint argb = NativeMethods.GetSystemAccentArgb();
        return Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }

    private void ApplyIconAccent()
    {
        DockIconControl.AccentDefault = _accentColor; // 新建控件（RenderIcons）直接取该值
        foreach (DockIconControl control in _iconControls)
        {
            control.SetAccent(_accentColor);
        }
    }

    /// <summary>
    /// FR-101 倒影：把图标区底部一段镜像绘制到胶囊内下方。
    /// 用 VisualBrush + 绝对 Viewbox 精确裁出底部 N px，再对矩形做 Y 轴翻转。
    /// </summary>
    private void UpdateReflection(ReflectionConfig reflection)
    {
        int height = Math.Clamp(reflection.Height,
            ConfigRanges.ReflectionHeight.Min, ConfigRanges.ReflectionHeight.Max);
        double strength = Math.Clamp(reflection.Strength,
            ConfigRanges.ReflectionStrength.Min, ConfigRanges.ReflectionStrength.Max);
        double panelWidth = IconsPanel.ActualWidth;
        double panelHeight = IconsPanel.ActualHeight;

        if (!reflection.Enabled || height <= 0 || strength <= 0.01
            || panelWidth <= 1 || panelHeight <= 1)
        {
            ReflectionHost.Visibility = Visibility.Collapsed;
            ReflectionHost.Height = 0;
            ReflectionVisual.Fill = null;
            return;
        }

        double crop = Math.Min(height, panelHeight);
        // 注意：VisualBrush 一旦绑定到**活的 Visual**（IconsPanel）就永远 CanFreeze=false，
        // 强行 Freeze 会在 SizeChanged 里抛 InvalidOperationException（2026-11-10 实机崩溃根因）。
        // 不冻结反而是想要的行为：图标变化时倒影自动跟随。
        var brush = new VisualBrush(IconsPanel)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, panelHeight - crop, panelWidth, crop),
        };

        // 倒影自上而下淡出（OpacityMask 是 UIElement 的属性，不是 Brush 的）。
        ReflectionVisual.OpacityMask = new LinearGradientBrush(
            Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(0, 255, 255, 255),
            new Point(0, 0), new Point(0, 1));

        ReflectionVisual.Fill = brush;
        ReflectionVisual.Width = panelWidth;
        ReflectionVisual.Height = crop;
        ReflectionHost.Height = crop;
        ReflectionHost.Opacity = strength;
        ReflectionHost.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// macOS 风格半透明毛玻璃背景。
    /// 关键结论（GitHub 源码审计 + 实机验证）：WPF 的 AllowsTransparency=True（分层窗口）
    /// 永远不会被 DWM 背景接管；且 Win11 上 ACCENT_ENABLE_BLURBEHIND 虽返回成功却不绘制
    /// （Sundew.Xaml / ModernWPF 审计已确认）。可靠做法（slimenull.github.io 验证）：
    ///   1. 窗口为普通窗口（AllowsTransparency=False）+ WindowStyle=None + 透明底色；
    ///   2. CompositionTarget.BackgroundColor=Transparent，避免 WPF 自绘底色盖住玻璃；
    ///   3. DwmExtendFrameIntoClientArea 把整窗扩展为玻璃边框 → Win11 整窗透出模糊桌面，
    ///      且始终绘制（不依赖窗口是否前台），适合常驻 Dock；
    ///   4. DWMWA_WINDOW_CORNER_PREFERENCE=ROUND 让 DWM 把窗口四角裁圆。
    ///   5. mica 模式改为 DWMWA_SYSTEMBACKDROP_TYPE=MAINWINDOW（需求 4.2 的官方 Mica）。
    /// 必须在 Loaded（窗口已上屏）后调用，SourceInitialized 时 DWM 会拒绝。
    /// </summary>
    /// <summary>
    /// 把窗口做成「真·毛玻璃」。
    /// </summary>
    /// <remarks>
    /// 关键教训：<c>DwmExtendFrameIntoClientArea(-1)</c> 只负责「让客户区可以透出 DWM 的
    /// backdrop」，它**本身不产生任何模糊**。在 Win10/11 上如果同时把
    /// <c>DWMWA_SYSTEMBACKDROP_TYPE</c> 设成 <c>DWMSBT_NONE</c>，透出来的就是未模糊的
    /// 桌面（多数机器上因为合成链路退化成黑色/灰色），观感就是「一块半透明灰板」——
    /// 用户反复反馈「看不到毛玻璃」的根因就在这里。
    /// 要得到模糊必须显式指定 backdrop 类型：Acrylic 实时模糊背后窗口内容（最接近 macOS
    /// Dock），Mica 采样壁纸。因此 acrylic / image → TRANSIENTWINDOW，mica → MAINWINDOW。
    /// </remarks>
    private void ApplyBackdrop()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        HwndSource? source = HwndSource.FromHwnd(handle);
        if (source is not null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        if (!NativeMethods.IsDwmCompositionEnabled())
        {
            // 8.3 虚拟机回退：无 DWM 时透明窗口会渲染成黑块，改为系统窗口底色。
            _log.Warn(Module, "DWM 合成未启用，毛玻璃不可用，回退纯色底");
            Background = SystemColors.WindowBrush;
            if (source is not null)
            {
                source.CompositionTarget.BackgroundColor = SystemColors.WindowColor;
            }

            return;
        }

        // 整窗扩展为玻璃边框（负值覆盖整个客户区）：客户区由此交给 DWM 合成，
        // WPF 不画底色的地方会露出 backdrop（真模糊由下面的 SYSTEMBACKDROP_TYPE 决定）。
        var margins = new NativeMethods.MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1,
        };
        int hr = NativeMethods.DwmExtendFrameIntoClientArea(handle, ref margins);
        if (hr != 0)
        {
            _log.Warn(Module, $"DwmExtendFrameIntoClientArea 失败 HRESULT=0x{hr:X8}");
        }

        // 深色语义：让 DWM 自动绘制的窗口边框/阴影走深色系，与胶囊一致。
        int dark = 1;
        NativeMethods.DwmSetWindowAttribute(handle,
            NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // 关掉 DWM 的 1px 系统边框。不关的话整窗透明的窗口在胶囊外侧会露出一圈细线，
        // 观感上像「Dock 下面压了一条黑边」。
        int noBorder = NativeMethods.DWMWA_COLOR_NONE;
        int borderResult = NativeMethods.DwmSetWindowAttribute(handle,
            NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        if (borderResult != 0)
        {
            _log.Info(Module, $"DWMWA_BORDER_COLOR 不支持 HRESULT=0x{borderResult:X8}（旧版本系统，忽略）");
        }

        ApplySystemBackdrop(handle);

        // DWM 圆角：胶囊四角由 DWM 裁圆，配合玻璃得到 macOS 风格。
        int corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref corner, sizeof(int));
    }

    /// <summary>
    /// 关闭 DWM 系统 backdrop。
    /// </summary>
    /// <remarks>
    /// <c>DWMWA_SYSTEMBACKDROP_TYPE</c>（Acrylic/Mica）作用于**整个窗口矩形**，而本窗口比
    /// 胶囊大一圈（左右各约 28px、上方约 48px，用于容纳图标放大溢出与阴影）。一旦打开，
    /// 胶囊外侧会露出一整块矩形雾 —— 比不加更难看，且形状无法裁剪。
    /// 因此真模糊改由 <see cref="GlassBackgroundService"/> 在胶囊形状内自绘，
    /// 系统 backdrop 恒为 NONE；<c>DwmExtendFrameIntoClientArea</c> 只用于让客户区可透明。
    /// </remarks>
    private void ApplySystemBackdrop(IntPtr handle)
    {
        int value = NativeMethods.DWMSBT_NONE;
        // 失败只可能是 Win11 21H2 之前没有该属性，默认行为本就是"不画 backdrop"，可忽略。
        NativeMethods.DwmSetWindowAttribute(handle,
            NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));
    }

    /// <summary>
    /// 安排一次玻璃重建。滑入/放大/换屏会连续改窗口位置与尺寸，
    /// 用去抖把这一串变化收敛成一次抓屏。
    /// </summary>
    private void ScheduleGlassRefresh(bool force = false)
    {
        if (force)
        {
            _glass.Invalidate();
        }

        if (_glassDebounce is null)
        {
            _glassDebounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(220),
            };
            _glassDebounce.Tick += (_, _) =>
            {
                _glassDebounce?.Stop();
                UpdateGlassBackground();
            };
        }

        _glassDebounce.Stop();
        _glassDebounce.Start();
    }

    /// <summary>
    /// 抓取胶囊背后的屏幕画面并模糊，作为胶囊背景；模糊严格落在胶囊形状内。
    /// </summary>
    /// <remarks>
    /// 坐标全程用物理像素：<c>BitBlt</c> 抓的是物理像素，而 WPF 布局是 DIP，
    /// 因此「窗口物理位置（GetWindowRect）+ 胶囊相对窗口的 DIP 偏移 × 本窗 DPI」。
    /// 不用 <c>PointToScreen</c> —— 实测它对 Dock 窗口有系统性偏移（记忆：-164,-247）。
    /// 抓取区域向四周外扩 <c>blur + 4</c> 作为模糊过渡，并与虚拟屏幕求交，
    /// 避免贴边 Dock 抓到屏幕外的未定义像素。
    /// </remarks>
    private void UpdateGlassBackground()
    {
        if (SystemParameters.HighContrast
            || string.Equals(_materialMode, "solid", StringComparison.Ordinal)
            || !_glass.IsAvailable)
        {
            return;
        }

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || Capsule.ActualWidth < 1 || Capsule.ActualHeight < 1)
        {
            return;
        }

        if (!NativeMethods.GetWindowRect(handle, out NativeMethods.RECT windowRect))
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Point origin = Capsule.TranslatePoint(new Point(0, 0), this);

        int cx = windowRect.Left + (int)Math.Round(origin.X * dpi.DpiScaleX);
        int cy = windowRect.Top + (int)Math.Round(origin.Y * dpi.DpiScaleY);
        int cw = (int)Math.Round(Capsule.ActualWidth * dpi.DpiScaleX);
        int ch = (int)Math.Round(Capsule.ActualHeight * dpi.DpiScaleY);
        if (cw <= 0 || ch <= 0)
        {
            return;
        }

        double radiusPx = GlassBlurRadius() * dpi.DpiScaleX;
        int pad = (int)Math.Ceiling(radiusPx) + 4;

        int virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int virtualWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int virtualHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        int grabLeft = Math.Max(virtualLeft, cx - pad);
        int grabTop = Math.Max(virtualTop, cy - pad);
        int grabRight = Math.Min(virtualLeft + virtualWidth, cx + cw + pad);
        int grabBottom = Math.Min(virtualTop + virtualHeight, cy + ch + pad);
        if (grabRight <= grabLeft || grabBottom <= grabTop)
        {
            return;
        }

        BitmapSource? blurred = _glass.Render(handle, grabLeft, grabTop,
            grabRight - grabLeft, grabBottom - grabTop, radiusPx);
        if (blurred is null)
        {
            // 抓屏不可用（全屏独占 / 旧系统）：停在 ApplyMaterial 铺的厚 tint 上。
            _log.Info(Module, "玻璃背景抓屏不可用，胶囊回退半透明纯色");
            return;
        }

        var brush = new ImageBrush(blurred)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(cx - grabLeft, cy - grabTop, cw, ch),
        };
        brush.Freeze();

        Capsule.Background = brush;
        // 薄 tint 叠在模糊图上：太少图标会糊在背景里，太多就成了深色板。
        TintLayer.Background = new SolidColorBrush(ResolveCapsuleTint(_tintAlpha));

        // 抓屏期间窗口被 SW_HIDE 过：WPF 的呈现目标会停摆，SW_SHOW 之后
        // 不一定自己重绘（实测表现为"窗口可见但画面空白"），这里强制一帧。
        InvalidateVisual();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => ApplyBackdrop();

    private static SolidColorBrush ParseBorderBrush(string value)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value)!;
            return new SolidColorBrush(color);
        }
        catch (FormatException)
        {
            return new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255));
        }
        catch (NotSupportedException)
        {
            return new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255));
        }
    }
}
