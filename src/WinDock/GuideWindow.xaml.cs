using System.Windows;
using System.Windows.Controls;
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

        // XAML 中控件默认值会在 InitializeComponent 期间触发事件，
        // 此时后续字段仍为 null；必须等构造完成再响应。
        _ready = true;
        BuildPresetCombo();
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

        SkipButton.Content = Loc.Instance.T("Guide_Skip");
        BackButton.Content = Loc.Instance.T("Guide_Back");
    }

    /// <summary>预设下拉与设置窗口同源（ThemePresets.Names），文本复用 Settings_Preset* 资源。</summary>
    private void BuildPresetCombo()
    {
        int selected = PresetCombo.SelectedIndex;
        PresetCombo.Items.Clear();
        foreach (string name in ThemePresets.Names)
        {
            PresetCombo.Items.Add(new ComboBoxItem
            {
                Content = Loc.Instance.T(PresetTextKey(name)),
                Tag = name,
            });
        }

        PresetCombo.SelectedIndex = selected >= 0 ? selected : 1; // 默认 macOS 深色
        PresetCombo.SelectionChanged += (_, _) =>
        {
            if (_ready)
            {
                UpdateCapsule();
            }
        };
    }

    private static string PresetTextKey(string preset) => preset switch
    {
        "macosLight" => "Settings_PresetMacosLight",
        "macosDark" => "Settings_PresetMacosDark",
        "mono" => "Settings_PresetMono",
        "cyber" => "Settings_PresetCyber",
        _ => "Settings_PresetGlass",
    };

    private string SelectedPreset =>
        (PresetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "macosDark";

    private void ApplyStep()
    {
        Step1.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = Loc.Instance.T("Guide_Step", _step + 1);
        BackButton.IsEnabled = _step > 0;
        NextButton.Content = Loc.Instance.T(_step == 2 ? "Guide_Start" : "Guide_Next");
    }

    /// <summary>预设只改本窗口预览胶囊；真正写配置在完成的最后一次 Save。</summary>
    private void UpdateCapsule()
    {
        // 用一份临时配置跑真实预设，预览与最终结果必然一致（不另起一套参数）。
        var preview = new DockConfig();
        ThemePresets.Apply(preview, SelectedPreset);
        byte alpha = (byte)Math.Clamp(preview.Appearance.Opacity * 255, 20, 255);
        PresetCapsule.Background = new SolidColorBrush(Color.FromArgb(alpha, 28, 30, 38));
        PresetCapsule.CornerRadius = new CornerRadius(preview.Dock.CornerRadius);
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
            // 5.3：第 2 步由用户选择任务栏模式。replace（隐藏原生任务栏）无官方 API，
            // 需求 R1 要求「明确告知 + 一键恢复兜底」，因此这里如实开放但保留托盘恢复入口。
            config.General.TaskbarMode = ModeReplace.IsChecked == true ? "replace"
                : ModeCoexist.IsChecked == true ? "coexist"
                : "beautify";

            // 6.4 预设主题：与设置窗口同一套参数。
            ThemePresets.Apply(config, SelectedPreset);
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
