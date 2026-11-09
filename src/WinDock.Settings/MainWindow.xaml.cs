using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;

namespace WinDock.Settings;

/// <summary>
/// 基础设置项（WD-010）：5 个外观参数 + 开机自启，带实时预览。
/// 修改后 150ms 去抖写盘，主进程由 ConfigService 的 FileSystemWatcher 热更新（≤200ms）。
/// </summary>
public partial class MainWindow : Window
{
    private const string Module = "Settings";

    private readonly IConfigService _config;
    private readonly ILogService _log;
    private Timer? _saveTimer;
    private bool _loading;

    public MainWindow(IConfigService config, ILogService log)
    {
        InitializeComponent();
        _config = config;
        _log = log;

        ApplyTexts();
        LoadValues();

        IconSizeSlider.ValueChanged += (_, _) => OnValueChanged();
        IconSpacingSlider.ValueChanged += (_, _) => OnValueChanged();
        CornerSlider.ValueChanged += (_, _) => OnValueChanged();
        OpacitySlider.ValueChanged += (_, _) => OnValueChanged();
        MagnifySlider.ValueChanged += (_, _) => OnValueChanged();
        StartupCheckBox.Checked += (_, _) => OnValueChanged();
        StartupCheckBox.Unchecked += (_, _) => OnValueChanged();
        PreviewCheckBox.Checked += (_, _) => OnValueChanged();
        PreviewCheckBox.Unchecked += (_, _) => OnValueChanged();

        ResetButton.Click += (_, _) => ResetToDefaults();
        GuideButton.Click += (_, _) => ScheduleGuideReplay();
        LogButton.Click += (_, _) => OpenLogFolder();
        CloseButton.Click += (_, _) => Close();
        Closed += (_, _) => _saveTimer?.Dispose();
    }

    private void ApplyTexts()
    {
        Title = Loc.Instance.T("Settings_Title");
        TitleText.Text = Title;
        PreviewLabel.Text = Loc.Instance.T("Settings_Preview");
        AppearanceGroup.Header = Loc.Instance.T("Settings_Appearance");
        BehaviorGroup.Header = Loc.Instance.T("Settings_Behavior");

        IconSizeLabel.Text = Loc.Instance.T("Settings_IconSize");
        IconSpacingLabel.Text = Loc.Instance.T("Settings_IconSpacing");
        CornerLabel.Text = Loc.Instance.T("Settings_CornerRadius");
        OpacityLabel.Text = Loc.Instance.T("Settings_Opacity");
        MagnifyLabel.Text = Loc.Instance.T("Settings_MagnifyScale");
        StartupCheckBox.Content = Loc.Instance.T("Settings_LaunchAtStartup");
        PreviewCheckBox.Content = Loc.Instance.T("Settings_PreviewEnabled");
        GuideButton.Content = Loc.Instance.T("Settings_ReplayGuide");

        ResetButton.Content = Loc.Instance.T("Settings_Reset");
        LogButton.Content = Loc.Instance.T("Settings_OpenLog");
        CloseButton.Content = Loc.Instance.T("Settings_Close");
    }

    private void LoadValues()
    {
        _loading = true;
        DockConfig config = _config.Current;
        IconSizeSlider.Value = config.Dock.IconSize;
        IconSpacingSlider.Value = config.Dock.IconSpacing;
        CornerSlider.Value = config.Dock.CornerRadius;
        OpacitySlider.Value = config.Appearance.Opacity * 100;
        MagnifySlider.Value = config.Animation.MagnifyScale;
        StartupCheckBox.IsChecked = config.General.LaunchAtStartup;
        PreviewCheckBox.IsChecked = config.Behavior.PreviewEnabled;
        _loading = false;
        UpdateLabelsAndPreview();
    }

    private void OnValueChanged()
    {
        if (_loading)
        {
            return;
        }

        DockConfig config = _config.Current;
        config.Dock.IconSize = (int)IconSizeSlider.Value;
        config.Dock.IconSpacing = (int)IconSpacingSlider.Value;
        config.Dock.CornerRadius = (int)CornerSlider.Value;
        config.Appearance.Opacity = OpacitySlider.Value / 100.0;
        config.Animation.MagnifyScale = MagnifySlider.Value;
        config.General.LaunchAtStartup = StartupCheckBox.IsChecked == true;
        config.Behavior.PreviewEnabled = PreviewCheckBox.IsChecked == true;

        UpdateLabelsAndPreview();
        ScheduleSave();
    }

    private void UpdateLabelsAndPreview()
    {
        DockConfig config = _config.Current;
        IconSizeValue.Text = Loc.Instance.T("Settings_ValuePx", config.Dock.IconSize);
        IconSpacingValue.Text = Loc.Instance.T("Settings_ValuePx", config.Dock.IconSpacing);
        CornerValue.Text = Loc.Instance.T("Settings_ValuePx", config.Dock.CornerRadius);
        OpacityValue.Text = Loc.Instance.T("Settings_ValuePercent", (int)Math.Round(config.Appearance.Opacity * 100));
        MagnifyValue.Text = Loc.Instance.T("Settings_ValueTimes", config.Animation.MagnifyScale.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture));

        byte alpha = (byte)Math.Round(Math.Clamp(config.Appearance.Opacity, 0, 1) * 255);
        PreviewCapsule.Background = new SolidColorBrush(Color.FromArgb(alpha, 28, 30, 38));
        PreviewCapsule.CornerRadius = new CornerRadius(Math.Min(config.Dock.CornerRadius, 24));

        PreviewIcons.Children.Clear();
        double size = Math.Clamp(config.Dock.IconSize * 0.6, 20, 58);
        double radius = config.Dock.IconShape == "circle"
            ? size / 2
            : Math.Max(2, size * 0.17);
        for (int i = 0; i < 3; i++)
        {
            PreviewIcons.Children.Add(new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(Color.FromArgb(
                    90, (byte)(120 + (i * 35)), 150, (byte)(220 - (i * 30)))),
                Margin = new Thickness(config.Dock.IconSpacing / 4.0, 0,
                    config.Dock.IconSpacing / 4.0, 0),
            });
        }
    }

    private void ScheduleSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => Dispatcher.Invoke(() =>
        {
            try
            {
                _config.Save(_config.Current);
            }
            catch (Exception ex)
            {
                _log.Error(Module, "保存配置失败", ex);
            }
        }), null, 150, Timeout.Infinite);
    }

    /// <summary>设置 firstRunCompleted=false，下次启动由组合根重新弹出 3 步引导。</summary>
    private void ScheduleGuideReplay()
    {
        _saveTimer?.Dispose();
        try
        {
            DockConfig config = _config.Current;
            config.General.FirstRunCompleted = false;
            _config.Save(config);
            MessageBox.Show(this, Loc.Instance.T("Settings_GuideScheduled"),
                Loc.Instance.T("Settings_Title"), MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _log.Error(Module, "安排重新查看引导失败", ex);
        }
    }

    private void ResetToDefaults()
    {
        _saveTimer?.Dispose();
        try
        {
            _config.Save(DockConfig.CreateDefault());
            LoadValues();
        }
        catch (Exception ex)
        {
            _log.Error(Module, "恢复默认配置失败", ex);
        }
    }

    private static void OpenLogFolder()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinDock", "logs");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", directory)
        {
            UseShellExecute = true,
        });
    }
}
