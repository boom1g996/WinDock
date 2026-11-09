using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinDock.Controls;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;

namespace WinDock.Services;

/// <summary>拖拽手势所需的宿主窄接口：控制器不直接依赖 MainWindow 实现（需求 14.2）。</summary>
public interface IDockDragHost
{
    Grid Root { get; }
    Panel IconsPanel { get; }
    UIElement HitArea { get; }
    DockConfig Config { get; }
    void CancelPreview();
    void RefreshAfterDrop();
    void ResetHover();
}

/// <summary>
/// WD-016 拖拽交互（FR-302 / AC-06）：指针捕获 + 3px 插入指示 + 1.1 倍跟随 + 拖出 300ms 取消固定。
/// 实现修订：设计说明 §3 原计划用 WPF OLE DragDrop；悬浮分层窗口上其模态消息循环会
/// 与 AnimationEngine 的帧派发及 300ms 拖出判定互相牵制，故改为 pointer capture，
/// 所有延时仍走唯一 ITimerScheduler（M2 D3），不新增 DispatcherTimer / 全局钩子。
/// </summary>
public sealed class DockDragController
{
    private const string Module = "DragUI";
    private const string OutsideKey = "drag.outside";
    private const int OutsideDelayMs = 300;
    private const double StartThreshold = 6;
    private const double DragScale = 1.1;
    private const double OutsideTolerance = 8;

    private readonly IDockDragHost _host;
    private readonly IDockDragService _service;
    private readonly ITimerScheduler _timers;
    private readonly ILogService _log;
    private readonly Border _marker;

    private DockIconControl? _candidate;
    private DockIconControl? _source;
    private Point _startLocal;
    private double _startRootX;
    private int _pinnedIndex = -1;
    private int _insertChildIndex;
    private bool _dragging;
    private bool _outsideElapsed;

    public DockDragController(IDockDragHost host, IDockDragService service,
        ITimerScheduler timers, ILogService log)
    {
        _host = host;
        _service = service;
        _timers = timers;
        _log = log;

        _marker = new Border
        {
            Width = 3,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x7E, 0xC8, 0xFF)),
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 18),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _host.Root.Children.Add(_marker);
    }

    public bool IsDragging => _dragging;

    /// <summary>
    /// 本次按下→抬起期间抑制图标点击。WPF 的通用 PreviewMouseUp 可能先于
    /// 本控制器订阅的 PreviewMouseLeftButtonUp 到达图标，必须提前拦截，
    /// 否则拖放结束会顺带启动应用（第四批实机冒烟发现的真实缺陷）。
    /// </summary>
    public bool SuppressClick { get; private set; }

    /// <summary>拖拽中的源控件；MainWindow 的动画帧会跳过它，避免两套变换互相覆盖。</summary>
    public DockIconControl? DraggedControl => _source;

    public void OnPreviewMouseDown(DockIconControl control, MouseButtonEventArgs e)
    {
        SuppressClick = false; // 新的一次按下：清掉上一次可能残留的抑制位
        if (e.ChangedButton != MouseButton.Left
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        {
            return;
        }

        _candidate = control;
        _startLocal = e.GetPosition(control);
        _startRootX = e.GetPosition(_host.Root).X;
    }

    public void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            UpdateDrag(e);
            return;
        }

        if (_candidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            _candidate = null;
            return;
        }

        Point local = e.GetPosition(_candidate);
        if (Math.Abs(local.X - _startLocal.X) < StartThreshold
            && Math.Abs(local.Y - _startLocal.Y) < StartThreshold)
        {
            return;
        }

        BeginDrag(e);
    }

    public void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            _candidate = null;
            SuppressClick = false;
            return;
        }

        e.Handled = true; // 拖拽结束不能顺带触发「启动 / 激活」点击
        FinishDrop();
        SuppressClick = false;
    }

    /// <summary>Esc / 配置热更新 / 结构重建时取消手势，不写配置。</summary>
    public void Cancel()
    {
        if (!_dragging)
        {
            _candidate = null;
            return;
        }

        _log.Info(Module, "取消拖拽手势（Esc / 可视化树重建）");
        Cleanup();
    }

    private void BeginDrag(MouseEventArgs e)
    {
        if (_candidate is null)
        {
            return;
        }

        _source = _candidate;
        _candidate = null;
        _dragging = true;
        SuppressClick = true;
        _outsideElapsed = false;
        _pinnedIndex = _source.Item.Pinned is { } pinned
            ? _host.Config.Pinned.FindIndex(p => ReferenceEquals(p, pinned))
            : -1;

        _service.BeginDrag();
        _host.CancelPreview();
        _marker.Height = _host.Config.Dock.IconSize * 0.9;
        _marker.Visibility = Visibility.Visible;
        _host.Root.CaptureMouse();

        // 拖拽期间冻结邻域放大：所有图标回到 1.0，源图标由控制器独占变换。
        foreach (DockIconControl icon in Icons())
        {
            icon.SetFrame(1, 0, 0);
        }

        _source.SetDragVisual(DragScale, 0);
        _host.ResetHover();
        UpdateDrag(e);
        _log.Info(Module, $"开始拖拽：{_source.Item.DisplayName}（pinnedIndex={_pinnedIndex}）");
    }

    private void UpdateDrag(MouseEventArgs e)
    {
        if (_source is null)
        {
            return;
        }

        Point inPanel = e.GetPosition(_host.IconsPanel);
        _insertChildIndex = ComputeInsertIndex(inPanel);
        PositionMarker(_insertChildIndex);

        double dx = e.GetPosition(_host.Root).X - _startRootX;
        _source.SetDragVisual(DragScale, dx * 0.85); // 轻微滞后 = 吸入吸附

        Point inHitArea = e.GetPosition(_host.HitArea);
        bool outside = inHitArea.X < -OutsideTolerance
            || inHitArea.Y < -OutsideTolerance
            || inHitArea.X > _host.HitArea.RenderSize.Width + OutsideTolerance
            || inHitArea.Y > _host.HitArea.RenderSize.Height + OutsideTolerance;

        if (outside && !_outsideElapsed)
        {
            _timers.Schedule(OutsideKey, OutsideDelayMs, OnOutsideElapsed);
        }
        else if (!outside)
        {
            _timers.Cancel(OutsideKey);
        }
    }

    /// <summary>拖出 Dock 停留 300ms：仅固定项取消固定，运行项拖出不做任何变更。</summary>
    private void OnOutsideElapsed()
    {
        if (!_dragging)
        {
            return;
        }

        _outsideElapsed = true;
        int index = _pinnedIndex;
        Cleanup(); // 先释放控件引用：Remove 会触发配置热更新并重建可视化树
        if (index >= 0)
        {
            _service.Remove(index);
        }
    }

    private void FinishDrop()
    {
        int from = _pinnedIndex;
        int insert = Math.Min(Math.Max(0, _insertChildIndex), _host.Config.Pinned.Count);
        string path = _source?.Item.TargetPath ?? "";
        string name = _source?.Item.DisplayName ?? "";
        bool alreadyRemoved = _outsideElapsed;

        _log.Info(Module,
            $"拖放落点 from={from}, insert={insert}, removedOutside={alreadyRemoved}");
        Cleanup();
        if (alreadyRemoved)
        {
            return;
        }

        if (from >= 0)
        {
            _service.Reorder(from, insert);
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            _service.Pin(insert, path, name);
        }

        _host.RefreshAfterDrop();
    }

    private void Cleanup()
    {
        _timers.Cancel(OutsideKey);
        _dragging = false;
        _outsideElapsed = false;
        _candidate = null;
        _pinnedIndex = -1;
        _insertChildIndex = 0;

        if (_host.Root.IsMouseCaptured)
        {
            _host.Root.ReleaseMouseCapture();
        }

        _marker.Visibility = Visibility.Collapsed;
        _source?.ClearDragVisual();
        _source = null;
        _service.EndDrag();
        _host.ResetHover();
    }

    private IEnumerable<DockIconControl> Icons()
    {
        foreach (UIElement child in _host.IconsPanel.Children)
        {
            if (child is DockIconControl icon)
            {
                yield return icon;
            }
        }
    }

    private int ComputeInsertIndex(Point inPanel)
    {
        UIElementCollection children = _host.IconsPanel.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not FrameworkElement element)
            {
                continue;
            }

            try
            {
                double center = element.TransformToAncestor(_host.IconsPanel)
                    .Transform(new Point(element.ActualWidth / 2, 0)).X;
                if (inPanel.X < center)
                {
                    return i;
                }
            }
            catch (InvalidOperationException)
            {
                return children.Count; // 元素尚未挂载到可视化树
            }
        }

        return children.Count;
    }

    private void PositionMarker(int insertIndex)
    {
        UIElementCollection children = _host.IconsPanel.Children;
        double x = 0;
        if (insertIndex < children.Count && children[insertIndex] is FrameworkElement target)
        {
            x = RelativeLeft(target, 0);
        }
        else if (children.Count > 0 && children[^1] is FrameworkElement last)
        {
            x = RelativeLeft(last, last.ActualWidth);
        }

        _marker.RenderTransform = new TranslateTransform(x - 1.5, 0);
        _marker.Visibility = Visibility.Visible;
    }

    private double RelativeLeft(FrameworkElement element, double offset)
    {
        try
        {
            return element.TransformToAncestor(_host.Root)
                .Transform(new Point(offset, 0)).X;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }
}
