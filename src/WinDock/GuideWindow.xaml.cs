using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;

namespace WinDock;

/// <summary>
/// WD-017 / AC-01 首次启动 3 步引导：说明 → 共存模式 → 预设预览。
/// 默认模式 C（beautify，仅显示悬浮 Dock、不隐藏原生任务栏）；
/// 其余两种模式在 M2 明确不启用，界面置灰并如实标注，避免误导用户。
/// 完成时一次性写盘，主进程由 ConfigService 热更新立即应用（AC-09）。
/// </summary>
public partial class GuideWindow : Window
{
    private readonly IConfigService _config;
    private readonly ILogService _log;
    private int _step;
    private bool _completed;
    private bool _ready;

    public GuideWindow(IConfigService config, ILogService log)
    {
        InitializeComponent();
        _config = config;
        _log = log;

        // XAML 中 PresetClassic.IsChecked=True 会在 InitializeComponent 期间触发
        // Checked 事件，此时后面的 PresetClear 字段还是 null；必须等构造完成再响应。
        _ready = true;
        ApplyTexts();
        ApplyStep();
        UpdateCapsule();
        // Alt+F4 / 系统关闭等任何路径都视为「跳过」：必须落 firstRunCompleted，
        // 否则引导会每次启动重复弹出（冒烟用 WM_CLOSE 走的就是这条路径）。
        Closed += (_, _) => Complete(applyChoices: false, closeWindow: false);
    }

    public event EventHandler? Completed;

    private void ApplyTexts()
    {
        S1Title.Text = Loc.Instance.T("Guide_S1_Title");
        S1Text.Text = Loc.Instance.T("Guide_S1_Text");
        S2Title.Text = Loc.Instance.T("Guide_S2_Title");
        S2Text.Text = Loc.Instance.T("Guide_S2_Text");
        S2Hint.Text = Loc.Instance.T("Guide_S2_Hint");
        S3Title.Text = Loc.Instance.T("Guide_S3_Title");
        S3Text.Text = Loc.Instance.T("Guide_S3_Text");

        ModeBeautify.Content = Loc.Instance.T("Guide_Mode_Beautify");
        ModeCoexist.Content = Loc.Instance.T("Guide_Mode_Coexist");
        ModeReplace.Content = Loc.Instance.T("Guide_Mode_Replace");
        PresetSimple.Content = Loc.Instance.T("Guide_Preset_Simple");
        PresetClassic.Content = Loc.Instance.T("Guide_Preset_Classic");
        PresetClear.Content = Loc.Instance.T("Guide_Preset_Clear");

        SkipButton.Content = Loc.Instance.T("Guide_Skip");
        BackButton.Content = Loc.Instance.T("Guide_Back");
    }

    private void ApplyStep()
    {
        Step1.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = Loc.Instance.T("Guide_Step", _step + 1);
        BackButton.IsEnabled = _step > 0;
        NextButton.Content = Loc.Instance.T(_step == 2 ? "Guide_Start" : "Guide_Next");
    }

    private void OnPresetChanged(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            UpdateCapsule();
        }
    }

    /// <summary>预设只改本窗口预览胶囊；真正写配置在完成的最后一次 Save。</summary>
    private void UpdateCapsule()
    {
        byte alpha = PresetSimple?.IsChecked == true ? (byte)56
            : PresetClear?.IsChecked == true ? (byte)40
            : (byte)77;
        double radius = PresetSimple?.IsChecked == true ? 16
            : PresetClear?.IsChecked == true ? 26
            : 22;
        PresetCapsule.Background = new SolidColorBrush(Color.FromArgb(alpha, 28, 30, 38));
        PresetCapsule.CornerRadius = new CornerRadius(radius);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step < 2)
        {
            _step++;
            ApplyStep();
            return;
        }

        Complete(applyChoices: true);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
        {
            _step--;
            ApplyStep();
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Complete(applyChoices: false);

    private void Complete(bool applyChoices, bool closeWindow = true)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        DockConfig config = _config.Current;
        if (applyChoices)
        {
            // 设计决策 D1：M2 只启用模式 C；引导中不改 TaskbarMode，避免承诺未实机验证的隐藏。
            config.General.TaskbarMode = "beautify";
            if (PresetSimple.IsChecked == true)
            {
                config.Dock.IconSize = 40;
                config.Appearance.Opacity = 0.22;
                config.Dock.CornerRadius = 16;
            }
            else if (PresetClear.IsChecked == true)
            {
                config.Dock.IconSize = 56;
                config.Appearance.Opacity = 0.16;
                config.Dock.CornerRadius = 26;
            }
            else
            {
                config.Dock.IconSize = 48;
                config.Appearance.Opacity = 0.30;
                config.Dock.CornerRadius = 22;
            }
        }

        config.General.FirstRunCompleted = true;
        _config.Save(config);
        _log.Info("Guide", applyChoices ? "首次引导完成，已应用预设" : "首次引导已跳过");
        Completed?.Invoke(this, EventArgs.Empty);
        if (closeWindow && IsLoaded)
        {
            Close();
        }
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    // PreviewKeyDown 从 Window 向下隧道，无论焦点落在哪个子控件都能收到 Escape。
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Complete(applyChoices: false);
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
