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
public partial class MainWindow : Window, IDockWindowHost
{
    private const string Module = "Dock";

    private readonly IConfigService _config;
    private readonly ILogService _log;
    private readonly ITaskbarInterop _taskbar;
    private readonly IWindowEnumerator _enumerator;
    private readonly IIconService _icons;
    private readonly IAnimationEngine _animation;
    private readonly IAppLauncher _launcher;
    private readonly List<DockIconControl> _iconControls = new();
    private DockConfig _current;
    private string _lastStructure = "";

    // M2：自动隐藏宿主状态。_baseTop 是 Dock 完全显示时的目标 Y，
    // 滑动只改 _slideOffset，动画数值全部来自 IAnimationEngine 单一时间轴。
    private IAutoHideController? _autoHide;
    private IHotkeyService? _hotkey;
    private double _baseTop;
    private double _slideOffset;
    private bool _pointerOver;

    public MainWindow(IConfigService config, ILogService log, ITaskbarInterop taskbar,
        IWindowEnumerator enumerator, IIconService icons, IAnimationEngine animation,
        IAppLauncher launcher)
    {
        InitializeComponent();
        _config = config;
        _log = log;
        _taskbar = taskbar;
        _enumerator = enumerator;
        _icons = icons;
        _animation = animation;
        _launcher = launcher;
        _current = config.Current;

        _animation.Frame += OnAnimationFrame;
        Root.MouseEnter += OnRootMouseEnter;
        Root.MouseLeave += OnRootMouseLeave;
        KeyDown += OnWindowKeyDown;
        SourceInitialized += OnSourceInitialized;

        ApplyConfig(_current);
    }

    /// <summary>Dock 是否被用户手动隐藏（区别于窗口 Visibility）。</summary>
    public bool IsDockVisible { get; private set; } = true;

    /// <summary>全屏/演示模式下暂停图标动效，但保留滑入滑出（FR-603 / D7）。</summary>
    public bool AnimationsPaused { get; set; }

    /// <summary>组合根在窗口显示前绑定自动隐藏与快捷键，避免可见性状态竞态。</summary>
    public void BindAutoHide(IAutoHideController autoHide, IHotkeyService hotkey)
    {
        _autoHide = autoHide;
        _hotkey = hotkey;
        autoHide.ApplyConfig(_current);
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

    public double HiddenDistance => (ActualHeight > 0 ? ActualHeight : 126) + 8;

    public bool IsPointerOverDock => _pointerOver;

    public void SetSlideOffset(double dip)
    {
        if (Math.Abs(_slideOffset - dip) < 0.01)
        {
            return;
        }

        _slideOffset = dip;
        Top = _baseTop + dip;
    }

    public void SetVisible(bool visible)
    {
        IsDockVisible = visible;
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetInteractive(bool interactive) => IsHitTestVisible = interactive;

    private void OnRootMouseEnter(object sender, MouseEventArgs e)
    {
        _pointerOver = true;
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
            PointerLeft?.Invoke(this, EventArgs.Empty);
        }));
    }

    /// <summary>配置热更新入口：外观、布局、动画参数全部即时生效（AC-09）。</summary>
    public void ApplyConfig(DockConfig config)
    {
        _current = config;

        byte alpha = (byte)Math.Round(Math.Clamp(config.Appearance.Opacity, 0, 1) * 255);
        Capsule.Background = new SolidColorBrush(Color.FromArgb(alpha, 28, 30, 38));
        Capsule.BorderBrush = ParseBorderBrush(config.Appearance.BorderColor);
        Capsule.BorderThickness = new Thickness(config.Appearance.BorderWidth);
        Capsule.CornerRadius = new CornerRadius(config.Dock.CornerRadius);
        Capsule.Padding = new Thickness(
            config.Dock.Padding, config.Dock.Padding,
            config.Dock.Padding, config.Dock.Padding + 8);

        double grow = config.Dock.IconSize * (config.Animation.MagnifyScale - 1);
        Root.Margin = new Thickness(grow / 2 + 10, grow + 12, grow / 2 + 10, 0);

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

        // 尺寸 / 间距 / 形状变化必须重建可视化树，强制结构签名失效。
        _lastStructure = "";
        RebuildIcons();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Position));
        TryApplyAcrylic(config);
    }

    /// <summary>窗口枚举事件 / 配置变更后的重建入口（UI 线程安全）。</summary>
    public void RefreshItems()
    {
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
            UpdateIcons(items);
            SyncIndicators();
            return false;
        }

        _lastStructure = structure;
        RenderIcons(items);
        SyncIndicators();
        return true;
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
                Icon = _icons.GetIcon(pinned, config.Dock.IconSize),
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
                Icon = _icons.GetIcon(group.ExecutablePath, config.Dock.IconSize),
            });
        }

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
        IconsPanel.Children.Clear();
        _iconControls.Clear();
        DockConfig config = _current;

        foreach (DockItem item in items)
        {
            if (item.IsSeparator)
            {
                IconsPanel.Children.Add(new Border
                {
                    Width = 1,
                    Height = config.Dock.IconSize * 0.6,
                    Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
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
            control.Margin = new Thickness(config.Dock.IconSpacing / 2.0, 0,
                config.Dock.IconSpacing / 2.0, 0);
            control.MouseEnter += (_, _) => OnIconEnter(control);
            control.PreviewMouseUp += (_, args) => OnIconMouseUp(control, args);
            control.KeyDown += OnIconKeyDown;
            IconsPanel.Children.Add(control);
            _iconControls.Add(control);
        }

        ClearHover();
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
    }

    private void ClearHover()
    {
        _animation.SetHover(null, _iconControls.Count,
            _current.Dock.IconSize, _current.Dock.IconSpacing);
    }

    private void OnAnimationFrame(object? sender, AnimationFrameEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnAnimationFrame(sender, e));
            return;
        }

        // 滑入滑出通道始终生效（全屏强制隐藏也依赖它），其余通道在全屏时暂停。
        SetSlideOffset(e.SlideOffset);
        if (AnimationsPaused)
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
        control.ContextMenu = DockMenuBuilder.Build(
            control.Item, _config, _log, _launcher, RefreshItems,
            () => (Application.Current as App)?.ShowSettings(), HideDock);
        control.ContextMenu.PlacementTarget = control;
        control.ContextMenu.IsOpen = true;
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
        // SystemParameters.WorkArea 已按 DIP 给出并自动排除原生任务栏，
        // 无论任务栏在顶部还是底部都成立（M0 实测本机为顶部）。
        Rect work = SystemParameters.WorkArea;
        Left = work.Left + ((work.Width - ActualWidth) / 2.0);
        _baseTop = work.Bottom - ActualHeight - _current.Dock.OffsetFromEdge;
        Top = _baseTop + _slideOffset;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        HwndSource? source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);
        TryApplyAcrylic(_current);

        // 快捷键需要真实 HWND；ApplyConfig 先设定开关，Attach 再实际注册。
        _hotkey?.ApplyConfig(_current);
        _hotkey?.Attach(handle);
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

        return IntPtr.Zero;
    }

    private void TryApplyAcrylic(DockConfig config)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || config.Appearance.BackgroundMode != "acrylic")
        {
            return;
        }

        if (NativeMethods.DwmIsCompositionEnabled(out int composition) != 0 || composition == 0)
        {
            _log.Info(Module, "无 DWM 合成，Acrylic 回退为纯半透明");
            return;
        }

        if (!NativeMethods.HasExport("user32.dll", "SetWindowCompositionAttribute"))
        {
            _log.Warn(Module, "系统未导出 SetWindowCompositionAttribute，Acrylic 回退为纯半透明");
            return;
        }

        // 未文档化 API：先实测导出再调用。AllowsTransparency=True 的分层窗口上
        // 该效果可能不生效，因此只记录「尝试」，不承诺模糊一定渲染（M0 降级结论）。
        var policy = new NativeMethods.AccentPolicy
        {
            AccentState = NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = 0,
        };
        int size = Marshal.SizeOf<NativeMethods.AccentPolicy>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = NativeMethods.WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = size,
            };
            bool ok = NativeMethods.SetWindowCompositionAttribute(handle, ref data);
            _log.Info(Module, ok
                ? "已尝试启用系统 Acrylic 背板（分层窗口下可能被系统忽略）"
                : $"Acrylic 设置失败 Win32Error={Marshal.GetLastWin32Error()}，回退纯半透明");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

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
