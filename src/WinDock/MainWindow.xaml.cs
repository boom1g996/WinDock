using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinDock.Controls;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Native;
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
    private double _baseTop;
    private double _baseLeft;
    private double _slideOffset;
    private bool _pointerOver;
    private bool _animationsPaused;

    // WD-032：最小化事件钩子（映射到对应图标触发「落入 Dock」反馈）。
    private IntPtr _minimizeHook;
    private NativeMethods.WinEventProc? _minimizeProc;

    public MainWindow(IConfigService config, ILogService log, ITaskbarInterop taskbar,
        IWindowEnumerator enumerator, IIconService icons, IAnimationEngine animation,
        IAppLauncher launcher, IMetricsService? metrics = null)
    {
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

        ApplyConfig(_current);
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

        // 背景由 DWM 整窗毛玻璃提供（ApplyGlass）：WPF 胶囊一律透明，避免盖住玻璃层。
        Capsule.Background = Brushes.Transparent;

        Capsule.BorderBrush = ParseBorderBrush(config.Appearance.BorderColor);
        Capsule.BorderThickness = new Thickness(config.Appearance.BorderWidth);
        Capsule.CornerRadius = new CornerRadius(config.Dock.CornerRadius);
        Capsule.Padding = new Thickness(
            config.Dock.Padding, config.Dock.Padding,
            config.Dock.Padding, config.Dock.Padding + 8);

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
        ApplyGlass();
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
            items.Add(new DockItem
            {
                Key = pinned.Id,
                DisplayName = name,
                Pinned = pinned,
                Group = group,
                Icon = _icons.GetIcon(pinned, iconPx)
                       ?? (pinned.Type == "folder" ? CreateSystemGlyph("folder") : null),
            });
        }

        List<AppWindowGroup> running = groups
            .Where(g => !usedGroups.Contains(g.Key))
            .ToList();
        if (items.Count > 0 && running.Count > 0 && !items[^1].IsSeparator)
        {
            items.Add(new DockItem { IsSeparator = true, Key = "runtime-separator" });
        }

        foreach (AppWindowGroup group in running)
        {
            items.Add(new DockItem
            {
                Key = group.Key,
                DisplayName = group.DisplayName,
                Group = group,
                Icon = _icons.GetIcon(group.ExecutablePath, iconPx),
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
    /// 图标提取目标尺寸：放大倍率下图标会被 RenderTransform 放大到
    /// IconSize×MagnifyScale，按该值（取整 + 8px 余量、上限 256）提取，
    /// 让放大后的图标仍有足够像素、不发虚（用户反馈图标模糊）。
    /// </summary>
    private int RequestedIconPx()
    {
        double need = _current.Dock.IconSize * _current.Animation.MagnifyScale + 8;
        return (int)Math.Min(256, Math.Max(_current.Dock.IconSize, Math.Ceiling(need)));
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

    private void OpenPreview(DockIconControl control, AppWindowGroup group)
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

        Point physical = control.PointToScreen(new Point(control.ActualWidth / 2, 0));
        Matrix fromDevice = PresentationSource.FromVisual(control)?
            .CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        panel.Open(control.Item.DisplayName, targets, fromDevice.Transform(physical));
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

        if (Root.IsMouseOver || panel.IsPointerOver)
        {
            return;
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
                    // FR-304 窗口缩略图切换面板是 P1；M1 用窗口列表菜单代替（明确降级）
                    OpenMenu(control);
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
        string? monitorId = _current.Dock.MonitorIds?.FirstOrDefault();
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

        Closed += (_, _) =>
        {
            if (_minimizeHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_minimizeHook);
                _minimizeHook = IntPtr.Zero;
                _minimizeProc = null;
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
    /// macOS 风格半透明毛玻璃背景。
    /// 关键结论（GitHub 源码审计 + 实机验证）：WPF 的 AllowsTransparency=True（分层窗口）
    /// 永远不会被 DWM 背景接管；且 Win11 上 ACCENT_ENABLE_BLURBEHIND 虽返回成功却不绘制
    /// （Sundew.Xaml / ModernWPF 审计已确认）。可靠做法（slimenull.github.io 验证）：
    ///   1. 窗口为普通窗口（AllowsTransparency=False）+ WindowStyle=None + 透明底色；
    ///   2. CompositionTarget.BackgroundColor=Transparent，避免 WPF 自绘底色盖住玻璃；
    ///   3. DwmExtendFrameIntoClientArea 把整窗扩展为玻璃边框 → Win11 整窗透出模糊桌面，
    ///      且始终绘制（不依赖窗口是否前台），适合常驻 Dock；
    ///   4. DWMWA_WINDOW_CORNER_PREFERENCE=ROUND 让 DWM 把窗口四角裁圆。
    /// 必须在 Loaded（窗口已上屏）后调用，SourceInitialized 时 DWM 会拒绝。
    /// </summary>
    private void ApplyGlass()
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

        if (NativeMethods.DwmIsCompositionEnabled(out int composition) != 0 || composition == 0)
        {
            _log.Warn(Module, "DWM 合成未启用，毛玻璃不可用，回退纯透明");
            return;
        }

        // 整窗扩展为玻璃边框（负值覆盖整个客户区）：Win11 下整窗透出模糊桌面。
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
        else
        {
            _log.Info(Module, "已启用整窗毛玻璃背景（DwmExtendFrameIntoClientArea）");
        }

        // DWM 圆角：胶囊四角由 DWM 裁圆，配合玻璃得到 macOS 风格。
        int corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref corner, sizeof(int));
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => ApplyGlass();

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
