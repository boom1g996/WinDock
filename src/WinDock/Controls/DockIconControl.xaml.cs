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

    public DockIconControl(DockItem item)
    {
        InitializeComponent();
        Bind(item);
    }

    /// <summary>绑定的视图模型。</summary>
    public DockItem Item { get; private set; } = null!;

    /// <summary>
    /// 增量刷新：窗口集合变化时只更新绑定数据与指示点，不重建可视化树。
    /// 全量重建会让 MouseLeave / hover 状态丢失并触发额外一轮动画。
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

    /// <summary>应用动画帧；缩放与水平位移由 AnimationEngine 统一计算。</summary>
    public void SetFrame(double scale, double offset)
    {
        Scale.ScaleX = scale;
        Scale.ScaleY = scale;
        Translate.X = offset;
    }

    /// <summary>运行指示点：4px 圆点，前台应用更亮（需求 6.1 / 已批复默认值 3）。</summary>
    public void SetIndicator(bool running, bool foreground)
    {
        Indicator.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        Indicator.Opacity = foreground ? 1.0 : 0.55;
        Indicator.Fill = foreground ? ForegroundDot : BackgroundDot;
    }
}
