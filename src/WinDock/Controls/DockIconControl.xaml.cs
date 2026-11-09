using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using WinDock.Core.Models;

namespace WinDock.Controls;

/// <summary>单个 Dock 图标：图像、圆角形状、运行指示点与逐帧变换。</summary>
public partial class DockIconControl : UserControl
{
    private static readonly Brush ForegroundDot = CreateFrozenBrush(0xF2, 0xF4, 0xF8);
    private static readonly Brush BackgroundDot = CreateFrozenBrush(0xC4, 0xC9, 0xD2);

    private bool _running;
    private bool _foreground;

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
    }

    /// <summary>应用动画帧；缩放、水平位移与跳动都由 AnimationEngine 统一计算。</summary>
    public void SetFrame(double scale, double offset, double bounceY = 0)
    {
        Scale.ScaleX = scale;
        Scale.ScaleY = scale;
        Translate.X = offset;
        Translate.Y = bounceY;
    }

    /// <summary>运行指示点目标：4px 圆点，前台应用更亮（需求 6.1 / 已批复默认值 3）。</summary>
    public void SetIndicator(bool running, bool foreground)
    {
        _running = running;
        _foreground = foreground;
        Indicator.Fill = foreground ? ForegroundDot : BackgroundDot;
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
