using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WinDock.Core.Models;

namespace WinDock.Controls;

/// <summary>单个 Dock 图标：图像、圆角形状、运行指示点与逐帧变换。</summary>
public partial class DockIconControl : UserControl
{
    private static readonly Brush ForegroundDot = CreateFrozenBrush(0xF2, 0xF4, 0xF8);
    private static readonly Brush BackgroundDot = CreateFrozenBrush(0xC4, 0xC9, 0xD2);

    /// <summary>拖拽态投影（FR-302）；冻结后可在多个控件间安全共享。</summary>
    private static readonly DropShadowEffect DragShadow = CreateFrozenShadow();

    private bool _running;
    private bool _foreground;

    // WD-022：垂直 Dock（左/右）时放大沿垂直方向生长、跳动朝屏幕内侧。
    private bool _vertical;
    private int _bounceSign = 1;

    public DockIconControl(DockItem item)
    {
        InitializeComponent();
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
        AutomationProperties.SetName(this, item.DisplayName);
        SetIndicator(item.IsRunning, item.IsForeground);
    }

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
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

    /// <summary>运行指示点目标：4px 圆点，前台应用更亮（需求 6.1 / 已批复默认值 3）。</summary>
    public void SetIndicator(bool running, bool foreground)
    {
        _running = running;
        _foreground = foreground;
        Indicator.Fill = foreground ? ForegroundDot : BackgroundDot;
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
        Indicator.Opacity = value * (_foreground ? 1.0 : 0.55);
        IndicatorScale.ScaleX = value;
        IndicatorScale.ScaleY = value;
    }
}
