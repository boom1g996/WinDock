using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Native;

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
    private readonly List<Button> _closeButtons = new();
    private readonly List<WindowPreviewTarget> _targets = new();
    private readonly uint _dumpPreviewMessage =
        NativeMethods.RegisterWindowMessage("WinDock.DumpPreview.v1");
    private string _headerApp = "";
    private bool _open;

    public PreviewWindow(IPreviewService preview, IAppLauncher launcher, ILogService log)
    {
        InitializeComponent();
        _preview = preview;
        _launcher = launcher;
        _log = log;

        SizeChanged += (_, _) => ApplyThumbnailLayout();
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _open = false;
            _preview.Hide(); // 无论何种关闭路径都必须注销 HTHUMBNAIL（FR-404 / R7）
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        HwndSource? source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);

        // 10 UI 规范：面板圆角 14px。WPF 的 Border 只负责内容裁切，
        // 窗口本身仍是矩形，必须让 DWM 把 HWND 也裁成圆角，
        // 否则四角会露出窗口底色（DWM 缩略图要求非分层窗口，不能靠透明背景解决）。
        int corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref corner, sizeof(int));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == unchecked((int)_dumpPreviewMessage))
        {
            DumpPreviewLayout(wParam.ToInt64());
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>鼠标进入面板：由宿主取消关闭倒计时。</summary>
    public event EventHandler? HoverEntered;

    /// <summary>鼠标离开面板：由宿主启动 300ms 关闭倒计时。</summary>
    public event EventHandler? HoverLeft;

    /// <summary>点击缩略图并激活目标窗口后触发。</summary>
    public event EventHandler? ThumbnailActivated;

    /// <summary>点击关闭按钮、WM_CLOSE 已投递后触发（宿主刷新运行指示与剩余窗口）。</summary>
    public event EventHandler<WindowPreviewTarget>? WindowClosed;

    public bool IsOpen => _open;

    /// <summary>鼠标是否停留在面板上（宿主判定「Dock + 面板」联合区域用）。</summary>
    public bool IsPointerOver => IsMouseOver;

    /// <param name="anchorTopDip">图标顶边中点的屏幕坐标（DIP），面板在其正上方居中。</param>
    /// <param name="hidden">超出预览上限未展开的窗口数（FR-402「超出显示 +N」）。</param>
    public void Open(string appName, IReadOnlyList<WindowPreviewTarget> targets,
        Point anchorTopDip, int hidden = 0)
    {
        _targets.Clear();
        _targets.AddRange(targets);
        _headerApp = appName;
        BuildItems(targets);
        HeaderText.Text = hidden > 0
            ? $"{appName} · {Loc.Instance.T("Preview_WindowCount", targets.Count)}"
              + $" · {Loc.Instance.T("Preview_MoreWindows", hidden)}"
            : $"{appName} · {Loc.Instance.T("Preview_WindowCount", targets.Count)}";

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
        _closeButtons.Clear();
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
            cell.Children.Add(BuildLabelBar(captured));
            ItemsPanel.Children.Add(cell);
        }
    }

    /// <summary>
    /// WD-037 / AC-05：缩略图下方的 18px 标签条 = 标题 + 关闭按钮。
    /// 关闭按钮必须放在缩略图矩形之外：DWM 缩略图直接绘制在本窗口 HWND 上，
    /// 会盖住该矩形内的一切 WPF 元素（Header 文本同理置于矩形外）。
    /// 标签条同时承担标题展示，替代原 ToolTip。
    /// </summary>
    private Grid BuildLabelBar(WindowPreviewTarget target)
    {
        var bar = new Grid { Width = SurfaceWidth, Height = 18, Margin = new Thickness(0, 4, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(target.Title)
                ? Loc.Instance.T("Preview_Untitled")
                : target.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(220, 242, 244, 248)),
        };
        Grid.SetColumn(title, 0);
        bar.Children.Add(title);

        var close = new Button
        {
            Width = 18,
            Height = 18,
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Opacity = 0.75,
            ToolTip = Loc.Instance.T("Preview_CloseWindow"),
            Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 3.5 3.5 L 14.5 14.5 M 14.5 3.5 L 3.5 14.5"),
                Stroke = new SolidColorBrush(Color.FromArgb(230, 242, 244, 248)),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(close,
            Loc.Instance.T("Preview_CloseWindow"));
        close.Click += (_, _) => CloseTarget(target);
        _closeButtons.Add(close);
        Grid.SetColumn(close, 1);
        bar.Children.Add(close);
        return bar;
    }

    /// <summary>
    /// WD-037 自动化探针：把每个关闭按钮中心的屏幕物理坐标写入日志，
    /// 格式：`preview-layout nonce=N cells=2 0:(x,y) 1:(x,y)`。
    /// 坐标算法与 MainWindow.DumpIconLayout 一致（GetWindowRect + 布局偏移 × DPI），
    /// 不用 PointToScreen（对本类窗口实测有原点偏差）。
    /// </summary>
    private void DumpPreviewLayout(long nonce)
    {
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            NativeMethods.GetWindowRect(handle, out NativeMethods.RECT rect);

            var parts = new List<string>(_closeButtons.Count);
            for (int i = 0; i < _closeButtons.Count; i++)
            {
                Button button = _closeButtons[i];
                if (!button.IsLoaded || button.ActualWidth <= 0)
                {
                    continue;
                }

                Point local = button.TransformToAncestor(this)
                    .Transform(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                int cx = rect.Left + (int)Math.Round(local.X * dpi.DpiScaleX);
                int cy = rect.Top + (int)Math.Round(local.Y * dpi.DpiScaleY);
                parts.Add($"{i}:({cx},{cy})");
            }

            _log.Info(Module,
                $"preview-layout nonce={nonce} hwnd=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) " +
                $"cells={_closeButtons.Count} {string.Join(' ', parts)}");
        }
        catch (Exception ex)
        {
            _log.Warn(Module, $"导出预览布局失败：{ex.Message}");
        }
    }

    /// <summary>关闭单个窗口：投递 WM_CLOSE → 移除 cell → 重建 DWM 注册 → 剩 0 则关面板。</summary>
    private void CloseTarget(WindowPreviewTarget target)
    {
        var info = new AppWindowInfo
        {
            Handle = target.Handle,
            Title = target.Title,
            IsMinimized = target.IsMinimized,
        };
        if (!_launcher.CloseWindow(info))
        {
            _log.Warn(Module, Loc.Instance.T("Error_CloseFailed"));
            return;
        }

        _targets.Remove(target);
        WindowClosed?.Invoke(this, target);

        if (_targets.Count == 0)
        {
            ClosePanel();
            return;
        }

        // 重建 cell 与 DWM 注册（面板窗口本身不重建，避免整面板闪烁）；
        // SizeToContent 收缩后保持左缘锚定，居中漂移为短寿命浮层的已知取舍。
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        List<WindowPreviewTarget> remaining = _targets.ToList();
        BuildItems(remaining);
        _preview.Show(hwnd, remaining);
        HeaderText.Text =
            $"{_headerApp} · {Loc.Instance.T("Preview_WindowCount", remaining.Count)}";
        ApplyThumbnailLayout();
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
