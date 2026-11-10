using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WinDock.Core.Models;

namespace WinDock.Controls;

/// <summary>单个 Dock 图标：图像、圆角形状、运行指示点与逐帧变换。</summary>
public partial class DockIconControl : UserControl
{
    /// <summary>FR-205 悬停浮起高度（DIP）与过渡时长。</summary>
    private const double HoverLift = 2.0;
    private static readonly Duration HoverDuration = new(TimeSpan.FromMilliseconds(80));

    /// <summary>6.10 强调色（指示点 / hover 高亮），由 MainWindow 注入。</summary>
    private static Color _accent = Color.FromRgb(0x0A, 0x84, 0xFF);

    /// <summary>新建图标控件时使用的默认强调色（MainWindow 在应用材质时写入）。</summary>
    public static Color AccentDefault
    {
        get => _accent;
        set => _accent = value;
    }

    /// <summary>拖拽态投影（FR-302）；冻结后可在多个控件间安全共享。</summary>
    private static readonly DropShadowEffect DragShadow = CreateFrozenShadow();

    private readonly TranslateTransform _lift = new();
    private bool _running;
    private bool _foreground;
    private string _indicatorStyle = "dot";

    // WD-022：垂直 Dock（左/右）时放大沿垂直方向生长、跳动朝屏幕内侧。
    private bool _vertical;
    private int _bounceSign = 1;

    /// <summary>8.4 可访问性：暴露为 Button 控件类型，读屏可识别「可按」并朗读 Name。</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new DockIconAutomationPeer(this);

    public DockIconControl(DockItem item)
    {
        InitializeComponent();
        IconFrame.RenderTransform = _lift; // 只抬图标本体，指示点保持基线（FR-205）
        Bind(item);
    }

    /// <summary>绑定的视图模型。</summary>
    public DockItem Item { get; private set; } = null!;

    public bool IsRunning => _running;

    public bool IsForeground => _foreground;

    /// <summary>
    /// 增量刷新：窗口集合变化时只更新绑定数据与指示点目标状态，不重建可视化树。
    /// 全量重建会让 MouseLeave / hover 状态丢失并触发额外一轮动画。
    /// 指示点的实际透明度由 AnimationEngine 帧驱动（FR-204）。
    /// </summary>
    public void Bind(DockItem item)
    {
        Item = item;
        IconImage.Source = item.Icon;
        ToolTip = item.DisplayName;
        Label.Text = item.DisplayName;
        AutomationProperties.SetName(this, item.DisplayName);
        AutomationProperties.SetHelpText(this, item.DisplayName);
        AutomationProperties.SetAutomationId(this, item.Key);
        SetIndicator(item.IsRunning, item.IsForeground);
        SetError(item.Error);
    }

    private static DropShadowEffect CreateFrozenShadow()
    {
        var effect = new DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 4,
            Opacity = 0.75,
            Color = Colors.Black,
        };
        effect.Freeze();
        return effect;
    }

    public void ApplyConfig(DockConfig config)
    {
        double size = config.Dock.IconSize;
        Width = size;
        Height = size;
        IconFrame.CornerRadius = config.Dock.IconShape switch
        {
            "rect" => new CornerRadius(0),
            "circle" => new CornerRadius(size / 2.0),
            _ => new CornerRadius(Math.Max(2, size * 0.17)),
        };

        // WD-022：放大锚点随 Dock 位置变化——底部向上生长、左侧向右、右侧向左，
        // 避免整条 Dock 因放大而抖动（FR-201「图标底边对齐」的方位扩展）。
        string position = config.Dock.Position;
        _vertical = position is "left" or "right";
        _bounceSign = position == "left" ? -1 : 1; // 左侧 Dock 跳动向右、右侧向左（朝屏幕内侧）
        Root.RenderTransformOrigin = position switch
        {
            "left" => new Point(0, 0.5),
            "right" => new Point(1, 0.5),
            _ => new Point(0.5, 1),
        };

        // 指示点贴在排列方向的外侧：底部在下方，左侧在右侧，右侧在左侧。
        Indicator.Margin = position switch
        {
            "left" => new Thickness(0, 0, -8, 0),
            "right" => new Thickness(-8, 0, 0, 0),
            _ => new Thickness(0, 0, 0, -8),
        };
        Indicator.HorizontalAlignment = position switch
        {
            "left" => HorizontalAlignment.Right,
            "right" => HorizontalAlignment.Left,
            _ => HorizontalAlignment.Center,
        };
        Indicator.VerticalAlignment = position == "bottom" || position.Length == 0
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Center;

        SetIndicatorStyle(config.Animation.IndicatorStyle);
        SetLabelMode(config.Behavior.IconLabelMode);
        SetAccent(_accent);
    }

    /// <summary>6.10 强调色：用于运行指示点与悬停高亮。</summary>
    public void SetAccent(Color accent)
    {
        _accent = accent;
        RefreshIndicatorBrushes();
        HoverGlow.Background = new SolidColorBrush(Color.FromArgb(26, accent.R, accent.G, accent.B));
    }

    private void RefreshIndicatorBrushes()
    {
        // 前台应用用强调色实色，其余用同色降透明度（FR-204「更亮/更长」的强调色版本）。
        foreach (UIElement child in Indicator.Children)
        {
            if (child is FrameworkElement element)
            {
                element.Opacity = _foreground ? 1.0 : 0.55;
                if (child is Shape shape)
                {
                    shape.Fill = new SolidColorBrush(_accent);
                }
            }
        }
    }

    /// <summary>
    /// FR-102 多窗口指示样式：dot（默认 4px 圆点）/ wide（更宽的点）/ triple（3 点）。
    /// 需求要求「多窗口时点数量不增加，改用更宽的点或 3 点样式，可配」。
    /// </summary>
    public void SetIndicatorStyle(string style)
    {
        string next = style is "wide" or "triple" or "dot" ? style : "dot";
        if (_indicatorStyle == next && Indicator.Children.Count > 0)
        {
            return;
        }

        _indicatorStyle = next;
        Indicator.Children.Clear();
        Indicator.Orientation = _vertical ? Orientation.Vertical : Orientation.Horizontal;
        switch (next)
        {
            case "wide":
                Indicator.Children.Add(CreateDot(10, 3, round: true));
                break;
            case "triple":
                for (int i = 0; i < 3; i++)
                {
                    Ellipse dot = CreateDot(3, 3, round: true);
                    dot.Margin = new Thickness(1, 0, 1, 0);
                    Indicator.Children.Add(dot);
                }

                break;
            default:
                Indicator.Children.Add(CreateDot(4, 4, round: true));
                break;
        }

        RefreshIndicatorBrushes();
    }

    private static Ellipse CreateDot(double width, double height, bool round)
    {
        _ = round;
        return new Ellipse
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(_accent),
        };
    }

    /// <summary>8.4 名称显示：tooltip（默认）/ always（图标内常显）/ never。</summary>
    public void SetLabelMode(string mode)
    {
        bool always = string.Equals(mode, "always", StringComparison.OrdinalIgnoreCase);
        bool never = string.Equals(mode, "never", StringComparison.OrdinalIgnoreCase);
        LabelHost.Visibility = always ? Visibility.Visible : Visibility.Collapsed;
        bool hasTooltip = !never;
        if (hasTooltip)
        {
            ToolTip = Item.DisplayName;
        }
        else
        {
            ToolTip = null;
        }
    }

    /// <summary>10 UI 规范错误态：图标上角红点，原因写进 ToolTip（hover 可见）。</summary>
    public void SetError(string? reason)
    {
        bool hasError = !string.IsNullOrWhiteSpace(reason);
        ErrorDot.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        if (hasError)
        {
            ToolTip = $"{Item.DisplayName} — {reason}";
        }
    }

    /// <summary>应用动画帧；缩放、沿排列方向位移与跳动都由 AnimationEngine 统一计算。</summary>
    public void SetFrame(double scale, double offset, double bounceY = 0)
    {
        Scale.ScaleX = scale;
        Scale.ScaleY = scale;
        if (_vertical)
        {
            // 垂直 Dock：排列方向是 Y，跳动朝屏幕内侧（X）。
            Translate.Y = offset;
            Translate.X = bounceY * _bounceSign;
        }
        else
        {
            Translate.X = offset;
            Translate.Y = bounceY;
        }
    }

    /// <summary>
    /// FR-205 悬停浮起：图标整体上移 2px + 4% 亮度，80ms 过渡。
    /// 只动画 IconFrame 的平移与叠加层透明度，与 AnimationEngine 的缩放/位移互不覆盖。
    /// </summary>
    public void SetHoverVisual(bool hovered)
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        _lift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(hovered ? -HoverLift : 0, HoverDuration) { EasingFunction = ease });
        HoverGlow.BeginAnimation(OpacityProperty,
            new DoubleAnimation(hovered ? 0.04 : 0, HoverDuration) { EasingFunction = ease });
    }

    /// <summary>运行指示点目标：前台应用更亮/更实（需求 6.1 / 已批复默认值 3）。</summary>
    public void SetIndicator(bool running, bool foreground)
    {
        _running = running;
        _foreground = foreground;
        RefreshIndicatorBrushes();
    }

    /// <summary>
    /// WD-016 拖拽态：放大到 1.1、随鼠标轻微滞后移动（吸入感）、增强投影。
    /// 拖拽期间 MainWindow 会跳过该控件的动画帧，避免两套变换互相覆盖。
    /// </summary>
    public void SetDragVisual(double scale, double offsetX)
    {
        Scale.ScaleX = scale;
        Scale.ScaleY = scale;
        if (_vertical)
        {
            Translate.Y = offsetX;
            Translate.X = -_bounceSign * 4; // 抬起方向朝屏幕内侧
        }
        else
        {
            Translate.X = offsetX;
            Translate.Y = -4;
        }

        IconFrame.Effect = DragShadow;
    }

    /// <summary>结束拖拽：移除投影并把变换交回 AnimationEngine 收敛。</summary>
    public void ClearDragVisual()
    {
        IconFrame.Effect = null;
        SetFrame(1, 0, 0);
    }

    /// <summary>由动画帧驱动指示点：0→1 淡入并放大，1→0 淡出（FR-204）。</summary>
    public void SetIndicatorFrame(double opacity)
    {
        double value = Math.Clamp(opacity, 0, 1);
        Indicator.Visibility = value > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        Indicator.Opacity = value;
        IndicatorScale.ScaleX = value;
        IndicatorScale.ScaleY = value;
    }

    private sealed class DockIconAutomationPeer : FrameworkElementAutomationPeer
    {
        public DockIconAutomationPeer(DockIconControl owner) : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore()
            => AutomationControlType.Button;

        protected override string GetClassNameCore() => "DockIcon";

        protected override string GetNameCore()
        {
            string name = base.GetNameCore();
            return string.IsNullOrWhiteSpace(name) ? "Dock icon" : name;
        }

        protected override bool IsEnabledCore() => true;
    }
}
