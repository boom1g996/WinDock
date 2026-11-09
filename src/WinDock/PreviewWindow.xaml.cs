using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;

namespace WinDock;

/// <summary>
/// WD-014 / FR-401~404 窗口缩略图预览面板。
/// DWM 缩略图由 <see cref="IPreviewService"/> 独占注册到本窗口 HWND，
/// 本类只负责占位矩形、标题、点击激活与布局换算；延时调度由 MainWindow 走
/// 唯一的 ITimerScheduler（M2 D3），这里不自持任何 Timer。
/// </summary>
public partial class PreviewWindow : Window
{
    private const string Module = "PreviewPanel";
    private const double SurfaceWidth = 160;
    private const double SurfaceHeight = 90;

    private readonly IPreviewService _preview;
    private readonly IAppLauncher _launcher;
    private readonly ILogService _log;
    private readonly List<Border> _surfaces = new();
    private bool _open;

    public PreviewWindow(IPreviewService preview, IAppLauncher launcher, ILogService log)
    {
        InitializeComponent();
        _preview = preview;
        _launcher = launcher;
        _log = log;

        SizeChanged += (_, _) => ApplyThumbnailLayout();
        Closed += (_, _) =>
        {
            _open = false;
            _preview.Hide(); // 无论何种关闭路径都必须注销 HTHUMBNAIL（FR-404 / R7）
        };
    }

    /// <summary>鼠标进入面板：由宿主取消关闭倒计时。</summary>
    public event EventHandler? HoverEntered;

    /// <summary>鼠标离开面板：由宿主启动 300ms 关闭倒计时。</summary>
    public event EventHandler? HoverLeft;

    /// <summary>点击缩略图并激活目标窗口后触发。</summary>
    public event EventHandler? ThumbnailActivated;

    public bool IsOpen => _open;

    /// <summary>鼠标是否停留在面板上（宿主判定「Dock + 面板」联合区域用）。</summary>
    public bool IsPointerOver => IsMouseOver;

    /// <param name="anchorTopDip">图标顶边中点的屏幕坐标（DIP），面板在其正上方居中。</param>
    public void Open(string appName, IReadOnlyList<WindowPreviewTarget> targets,
        Point anchorTopDip)
    {
        BuildItems(targets);
        HeaderText.Text = $"{appName} · {Loc.Instance.T("Preview_WindowCount", targets.Count)}";

        Show();
        _open = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                Left = anchorTopDip.X - (ActualWidth / 2);
                Top = anchorTopDip.Y - ActualHeight - 14;

                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                _preview.Show(hwnd, targets);
                ApplyThumbnailLayout();
            }));
    }

    /// <summary>幂等关闭；Closed 事件会完成 DWM 注销。</summary>
    public void ClosePanel()
    {
        if (!_open)
        {
            return;
        }

        _preview.Hide();
        _open = false;
        Close();
    }

    private void BuildItems(IReadOnlyList<WindowPreviewTarget> targets)
    {
        _surfaces.Clear();
        ItemsPanel.Children.Clear();

        foreach (WindowPreviewTarget target in targets)
        {
            var cell = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
            var surface = new Border
            {
                Width = SurfaceWidth,
                Height = SurfaceHeight,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
            };
            _surfaces.Add(surface);

            var grid = new Grid { Width = SurfaceWidth, Height = SurfaceHeight };
            grid.Children.Add(surface);

            var button = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = target.Title,
                Focusable = false,
            };
            WindowPreviewTarget captured = target;
            button.Click += (_, _) => Activate(captured);
            grid.Children.Add(button);

            if (target.IsMinimized)
            {
                // FR-402：最小化窗口 DWM 源不刷新，用半透明遮罩 + 文案占位。
                grid.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(150, 12, 14, 20)),
                    CornerRadius = new CornerRadius(5),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = Loc.Instance.T("Preview_Minimized"),
                        Foreground = new SolidColorBrush(Color.FromArgb(210, 242, 244, 248)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
            }

            cell.Children.Add(grid);
            cell.Children.Add(new TextBlock
            {
                Width = SurfaceWidth,
                Margin = new Thickness(0, 5, 0, 0),
                Text = string.IsNullOrWhiteSpace(target.Title)
                    ? Loc.Instance.T("Preview_Untitled")
                    : target.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 242, 244, 248)),
            });
            ItemsPanel.Children.Add(cell);
        }
    }

    private void Activate(WindowPreviewTarget target)
    {
        var info = new AppWindowInfo
        {
            Handle = target.Handle,
            Title = target.Title,
            IsMinimized = target.IsMinimized,
        };
        if (!_launcher.Activate(info))
        {
            _log.Warn(Module, Loc.Instance.T("Error_ActivateFailed"));
        }

        ThumbnailActivated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把占位矩形从 DIP 换算为目标 HWND 客户区的设备像素坐标。</summary>
    private void ApplyThumbnailLayout()
    {
        if (!_open || _surfaces.Count == 0)
        {
            return;
        }

        Matrix toDevice = PresentationSource.FromVisual(this)?
            .CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var rects = new List<Rect>(_surfaces.Count);
        foreach (Border surface in _surfaces)
        {
            GeneralTransform transform = surface.TransformToVisual(this);
            Point origin = toDevice.Transform(transform.Transform(new Point(0, 0)));
            Point corner = toDevice.Transform(transform.Transform(
                new Point(surface.ActualWidth, surface.ActualHeight)));
            rects.Add(new Rect(origin.X, origin.Y,
                Math.Max(0, corner.X - origin.X), Math.Max(0, corner.Y - origin.Y)));
        }

        _preview.UpdateLayout(rects);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        HoverEntered?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverLeft?.Invoke(this, EventArgs.Empty);
    }
}
