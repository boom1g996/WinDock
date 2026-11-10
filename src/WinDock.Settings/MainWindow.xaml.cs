using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;

namespace WinDock.Settings;

/// <summary>
/// 全量设置项（WD-010 / WD-023 / WD-031 / WD-028）：外观 + 布局 + 材质 + 行为 + 高级，
/// 带实时预览；修改后 150ms 去抖写盘，主进程由 ConfigService 的 FileSystemWatcher 热更新。
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

        BuildCombos();
        ApplyTexts();
        LoadValues();
        WireEvents();
    }

    private sealed class ComboEntry
    {
        public ComboEntry(string text, string value)
        {
            Text = text;
            Tag = value;
        }

        public string Text { get; }
        public string Tag { get; }
        public override string ToString() => Text;
    }

    private static void Fill(ComboBox combo, params (string Text, string Value)[] items)
    {
        combo.SelectedValuePath = "Tag";
        combo.DisplayMemberPath = "Text";
        var list = new List<ComboEntry>(items.Length);
        foreach ((string text, string value) in items)
        {
            list.Add(new ComboEntry(text, value));
        }

        combo.ItemsSource = list;
    }

    private void BuildCombos()
    {
        Fill(PresetCombo,
            (Loc.Instance.T("Settings_PresetCustom"), "custom"),
            (Loc.Instance.T("Settings_PresetMacosLight"), "macosLight"),
            (Loc.Instance.T("Settings_PresetMacosDark"), "macosDark"),
            (Loc.Instance.T("Settings_PresetMono"), "mono"),
            (Loc.Instance.T("Settings_PresetCyber"), "cyber"),
            (Loc.Instance.T("Settings_PresetGlass"), "glass"));

        Fill(PositionCombo,
            (Loc.Instance.T("Settings_PosBottom"), "bottom"),
            (Loc.Instance.T("Settings_PosLeft"), "left"),
            (Loc.Instance.T("Settings_PosRight"), "right"));

        Fill(IconShapeCombo,
            (Loc.Instance.T("Settings_ShapeRect"), "rect"),
            (Loc.Instance.T("Settings_ShapeRounded"), "roundedRect"),
            (Loc.Instance.T("Settings_ShapeCircle"), "circle"));

        Fill(BlurCombo,
            (Loc.Instance.T("Settings_BlurLow"), "low"),
            (Loc.Instance.T("Settings_BlurMedium"), "medium"),
            (Loc.Instance.T("Settings_BlurHigh"), "high"));

        Fill(ShadowCombo,
            (Loc.Instance.T("Settings_ShadowNone"), "none"),
            (Loc.Instance.T("Settings_ShadowWeak"), "weak"),
            (Loc.Instance.T("Settings_ShadowMedium"), "medium"),
            (Loc.Instance.T("Settings_ShadowStrong"), "strong"));

        Fill(AccentCombo,
            (Loc.Instance.T("Settings_AccentSystem"), ""),
            ("macOS 蓝", "#FF0A84FF"),
            ("青", "#FF00E5FF"),
            ("绿", "#FF30D158"),
            ("橙", "#FFFF9F0A"),
            ("红", "#FFFF453A"),
            ("紫", "#FFBF5AF2"),
            ("白", "#FFFFFFFF"));

        Fill(ModeCombo,
            (Loc.Instance.T("Settings_ModeBeautify"), "beautify"),
            (Loc.Instance.T("Settings_ModeCoexist"), "coexist"),
            (Loc.Instance.T("Settings_ModeReplace"), "replace"));

        Fill(LanguageCombo,
            (Loc.Instance.T("Settings_LangZh"), "zh-CN"),
            (Loc.Instance.T("Settings_LangEn"), "en"));
    }

    private void ApplyTexts()
    {
        Title = Loc.Instance.T("Settings_Title");
        TitleText.Text = Title;
        PreviewLabel.Text = Loc.Instance.T("Settings_Preview");

        PresetLabel.Text = Loc.Instance.T("Settings_Preset");
        LayoutGroup.Header = Loc.Instance.T("Settings_GroupLayout");
        MaterialGroup.Header = Loc.Instance.T("Settings_GroupMaterial");
        BehaviorGroup.Header = Loc.Instance.T("Settings_Behavior");
        AdvancedGroup.Header = Loc.Instance.T("Settings_GroupAdvanced");

        PositionLabel.Text = Loc.Instance.T("Settings_Position");
        IconShapeLabel.Text = Loc.Instance.T("Settings_IconShape");
        IconSizeLabel.Text = Loc.Instance.T("Settings_IconSize");
        IconSpacingLabel.Text = Loc.Instance.T("Settings_IconSpacing");
        PaddingLabel.Text = Loc.Instance.T("Settings_Padding");
        CornerLabel.Text = Loc.Instance.T("Settings_CornerRadius");
        OffsetLabel.Text = Loc.Instance.T("Settings_OffsetFromEdge");

        BlurLabel.Text = Loc.Instance.T("Settings_BlurLevel");
        ShadowLabel.Text = Loc.Instance.T("Settings_ShadowLevel");
        AccentLabel.Text = Loc.Instance.T("Settings_AccentColor");
        ReflectionCheckBox.Content = Loc.Instance.T("Settings_Reflection");
        ReflectionStrengthLabel.Text = Loc.Instance.T("Settings_ReflectionStrength");
        OpacityLabel.Text = Loc.Instance.T("Settings_Opacity");
        MagnifyLabel.Text = Loc.Instance.T("Settings_MagnifyScale");

        ModeLabel.Text = Loc.Instance.T("Settings_TaskbarMode");
        LanguageLabel.Text = Loc.Instance.T("Settings_Language");
        StartupCheckBox.Content = Loc.Instance.T("Settings_LaunchAtStartup");
        PreviewCheckBox.Content = Loc.Instance.T("Settings_PreviewEnabled");
        MinimizeFeedbackCheckBox.Content = Loc.Instance.T("Settings_MinimizeFeedback");
        SystemAreaCheckBox.Content = Loc.Instance.T("Settings_SystemArea");
        RecycleBinCheckBox.Content = Loc.Instance.T("Settings_ShowRecycleBin");
        TaskViewCheckBox.Content = Loc.Instance.T("Settings_ShowTaskView");
        ShowDesktopCheckBox.Content = Loc.Instance.T("Settings_ShowShowDesktop");
        GuideButton.Content = Loc.Instance.T("Settings_ReplayGuide");

        ExportButton.Content = Loc.Instance.T("Settings_Export");
        ImportButton.Content = Loc.Instance.T("Settings_Import");
        UpdateButton.Content = Loc.Instance.T("Settings_UpdateCheck");

        ResetButton.Content = Loc.Instance.T("Settings_Reset");
        LogButton.Content = Loc.Instance.T("Settings_OpenLog");
        CloseButton.Content = Loc.Instance.T("Settings_Close");

        ApplyAutomationNames();
    }

    /// <summary>WD-026 / 8.4：为可操作控件补 UI Automation Name（ControlType/IsEnabled 由 WPF 提供）。</summary>
    private void ApplyAutomationNames()
    {
        void Name(System.Windows.UIElement element, string text) =>
            System.Windows.Automation.AutomationProperties.SetName(element, text);

        Name(PresetCombo, PresetLabel.Text);
        Name(PositionCombo, PositionLabel.Text);
        Name(IconShapeCombo, IconShapeLabel.Text);
        Name(BlurCombo, BlurLabel.Text);
        Name(ShadowCombo, ShadowLabel.Text);
        Name(AccentCombo, AccentLabel.Text);
        Name(ModeCombo, ModeLabel.Text);
        Name(LanguageCombo, LanguageLabel.Text);
        Name(IconSizeSlider, IconSizeLabel.Text);
        Name(IconSpacingSlider, IconSpacingLabel.Text);
        Name(PaddingSlider, PaddingLabel.Text);
        Name(CornerSlider, CornerLabel.Text);
        Name(OffsetSlider, OffsetLabel.Text);
        Name(OpacitySlider, OpacityLabel.Text);
        Name(MagnifySlider, MagnifyLabel.Text);
        Name(ReflectionStrengthSlider, ReflectionStrengthLabel.Text);
        Name(ReflectionCheckBox, (string)ReflectionCheckBox.Content);
        Name(StartupCheckBox, (string)StartupCheckBox.Content);
        Name(PreviewCheckBox, (string)PreviewCheckBox.Content);
        Name(MinimizeFeedbackCheckBox, (string)MinimizeFeedbackCheckBox.Content);
        Name(SystemAreaCheckBox, (string)SystemAreaCheckBox.Content);
        Name(RecycleBinCheckBox, (string)RecycleBinCheckBox.Content);
        Name(TaskViewCheckBox, (string)TaskViewCheckBox.Content);
        Name(ShowDesktopCheckBox, (string)ShowDesktopCheckBox.Content);
        Name(ExportButton, (string)ExportButton.Content);
        Name(ImportButton, (string)ImportButton.Content);
        Name(UpdateButton, (string)UpdateButton.Content);
        Name(ResetButton, (string)ResetButton.Content);
        Name(CloseButton, (string)CloseButton.Content);
    }

    private void LoadValues()
    {
        _loading = true;
        DockConfig config = _config.Current;
        DockLayoutConfig d = config.Dock;
        AppearanceConfig a = config.Appearance;
        BehaviorConfig b = config.Behavior;

        PresetCombo.SelectedValue = a.Preset;
        PositionCombo.SelectedValue = d.Position;
        IconShapeCombo.SelectedValue = d.IconShape;
        IconSizeSlider.Value = d.IconSize;
        IconSpacingSlider.Value = d.IconSpacing;
        PaddingSlider.Value = d.Padding;
        CornerSlider.Value = d.CornerRadius;
        OffsetSlider.Value = d.OffsetFromEdge;

        OpacitySlider.Value = a.Opacity * 100;
        BlurCombo.SelectedValue = a.BlurLevel;
        ShadowCombo.SelectedValue = a.ShadowLevel;
        AccentCombo.SelectedValue = string.IsNullOrWhiteSpace(a.AccentColor) ? "" : a.AccentColor;
        ReflectionCheckBox.IsChecked = a.Reflection.Enabled;
        ReflectionStrengthSlider.Value = a.Reflection.Strength * 100;
        MagnifySlider.Value = config.Animation.MagnifyScale;

        ModeCombo.SelectedValue = config.General.TaskbarMode;
        LanguageCombo.SelectedValue = config.General.Language;
        StartupCheckBox.IsChecked = config.General.LaunchAtStartup;
        PreviewCheckBox.IsChecked = b.PreviewEnabled;
        MinimizeFeedbackCheckBox.IsChecked = b.MinimizeFeedbackEnabled;
        SystemAreaCheckBox.IsChecked = b.ShowSystemArea;
        RecycleBinCheckBox.IsChecked = b.ShowRecycleBin;
        TaskViewCheckBox.IsChecked = b.ShowTaskView;
        ShowDesktopCheckBox.IsChecked = b.ShowShowDesktop;

        ReflectionStrengthSlider.IsEnabled = a.Reflection.Enabled;
        SyncSystemAreaEnabled();

        _loading = false;
        UpdateLabelsAndPreview();
    }

    private void WireEvents()
    {
        IconSizeSlider.ValueChanged += (_, _) => OnValueChanged();
        IconSpacingSlider.ValueChanged += (_, _) => OnValueChanged();
        PaddingSlider.ValueChanged += (_, _) => OnValueChanged();
        CornerSlider.ValueChanged += (_, _) => OnValueChanged();
        OffsetSlider.ValueChanged += (_, _) => OnValueChanged();
        OpacitySlider.ValueChanged += (_, _) => OnValueChanged();
        ReflectionStrengthSlider.ValueChanged += (_, _) => OnValueChanged();
        MagnifySlider.ValueChanged += (_, _) => OnValueChanged();

        PositionCombo.SelectionChanged += (_, _) => OnValueChanged();
        IconShapeCombo.SelectionChanged += (_, _) => OnValueChanged();
        BlurCombo.SelectionChanged += (_, _) => OnValueChanged();
        ShadowCombo.SelectionChanged += (_, _) => OnValueChanged();
        AccentCombo.SelectionChanged += (_, _) => OnValueChanged();
        ModeCombo.SelectionChanged += (_, _) => OnValueChanged();
        LanguageCombo.SelectionChanged += (_, _) => OnLanguageChanged();
        PresetCombo.SelectionChanged += (_, _) => OnPresetChanged();

        StartupCheckBox.Checked += (_, _) => OnValueChanged();
        StartupCheckBox.Unchecked += (_, _) => OnValueChanged();
        PreviewCheckBox.Checked += (_, _) => OnValueChanged();
        PreviewCheckBox.Unchecked += (_, _) => OnValueChanged();
        MinimizeFeedbackCheckBox.Checked += (_, _) => OnValueChanged();
        MinimizeFeedbackCheckBox.Unchecked += (_, _) => OnValueChanged();
        ReflectionCheckBox.Checked += (_, _) => OnReflectionToggled();
        ReflectionCheckBox.Unchecked += (_, _) => OnReflectionToggled();
        SystemAreaCheckBox.Checked += (_, _) => OnSystemAreaToggled();
        SystemAreaCheckBox.Unchecked += (_, _) => OnSystemAreaToggled();
        RecycleBinCheckBox.Checked += (_, _) => OnValueChanged();
        RecycleBinCheckBox.Unchecked += (_, _) => OnValueChanged();
        TaskViewCheckBox.Checked += (_, _) => OnValueChanged();
        TaskViewCheckBox.Unchecked += (_, _) => OnValueChanged();
        ShowDesktopCheckBox.Checked += (_, _) => OnValueChanged();
        ShowDesktopCheckBox.Unchecked += (_, _) => OnValueChanged();

        ResetButton.Click += (_, _) => ResetToDefaults();
        GuideButton.Click += (_, _) => ScheduleGuideReplay();
        LogButton.Click += (_, _) => OpenLogFolder();
        CloseButton.Click += (_, _) => Close();
        ExportButton.Click += (_, _) => ExportConfig();
        ImportButton.Click += (_, _) => ImportConfig();
        UpdateButton.Click += (_, _) => CheckUpdate();
        Closed += (_, _) => _saveTimer?.Dispose();
    }

    private void OnPresetChanged()
    {
        if (_loading)
        {
            return;
        }

        DockConfig config = _config.Current;
        string preset = PresetCombo.SelectedValue as string ?? "custom";
        if (!string.Equals(preset, "custom", StringComparison.Ordinal))
        {
            ThemePresets.Apply(config, preset);
            _loading = true;
            LoadValues();
            _loading = false;
        }
        else
        {
            config.Appearance.Preset = "custom";
        }

        UpdateLabelsAndPreview();
        ScheduleSave();
    }

    private void OnValueChanged()
    {
        if (_loading)
        {
            return;
        }

        DockConfig config = _config.Current;
        DockLayoutConfig d = config.Dock;
        AppearanceConfig a = config.Appearance;
        BehaviorConfig b = config.Behavior;

        d.Position = PositionCombo.SelectedValue as string ?? d.Position;
        d.IconShape = IconShapeCombo.SelectedValue as string ?? d.IconShape;
        d.IconSize = (int)IconSizeSlider.Value;
        d.IconSpacing = (int)IconSpacingSlider.Value;
        d.Padding = (int)PaddingSlider.Value;
        d.CornerRadius = (int)CornerSlider.Value;
        d.OffsetFromEdge = (int)OffsetSlider.Value;

        a.Opacity = OpacitySlider.Value / 100.0;
        a.BlurLevel = BlurCombo.SelectedValue as string ?? a.BlurLevel;
        a.ShadowLevel = ShadowCombo.SelectedValue as string ?? a.ShadowLevel;
        a.AccentColor = AccentCombo.SelectedValue as string ?? "";
        a.Reflection.Enabled = ReflectionCheckBox.IsChecked == true;
        a.Reflection.Strength = ReflectionStrengthSlider.Value / 100.0;
        config.Animation.MagnifyScale = MagnifySlider.Value;

        config.General.TaskbarMode = ModeCombo.SelectedValue as string ?? config.General.TaskbarMode;
        config.General.LaunchAtStartup = StartupCheckBox.IsChecked == true;
        b.PreviewEnabled = PreviewCheckBox.IsChecked == true;
        b.MinimizeFeedbackEnabled = MinimizeFeedbackCheckBox.IsChecked == true;
        b.ShowSystemArea = SystemAreaCheckBox.IsChecked == true;
        b.ShowRecycleBin = RecycleBinCheckBox.IsChecked == true;
        b.ShowTaskView = TaskViewCheckBox.IsChecked == true;
        b.ShowShowDesktop = ShowDesktopCheckBox.IsChecked == true;

        // 手动微调后不再跟随预设。
        if (!string.Equals(a.Preset, "custom", StringComparison.Ordinal))
        {
            a.Preset = "custom";
            _loading = true;
            PresetCombo.SelectedValue = "custom";
            _loading = false;
        }

        ReflectionStrengthSlider.IsEnabled = a.Reflection.Enabled;
        UpdateLabelsAndPreview();
        ScheduleSave();
    }

    private void OnReflectionToggled()
    {
        ReflectionStrengthSlider.IsEnabled = ReflectionCheckBox.IsChecked == true;
        OnValueChanged();
    }

    private void OnSystemAreaToggled()
    {
        SyncSystemAreaEnabled();
        OnValueChanged();
    }

    private void SyncSystemAreaEnabled()
    {
        bool on = SystemAreaCheckBox.IsChecked == true;
        RecycleBinCheckBox.IsEnabled = on;
        TaskViewCheckBox.IsEnabled = on;
        ShowDesktopCheckBox.IsEnabled = on;
    }

    private void OnLanguageChanged()
    {
        if (_loading)
        {
            return;
        }

        string lang = LanguageCombo.SelectedValue as string ?? "zh-CN";
        _config.Current.General.Language = lang;
        Loc.Instance.SetLanguage(lang);
        // 语言切换后按钮/标签需要按新语言重排；控件内容刷新前先保存。
        ScheduleSave();
        ApplyTexts();
        BuildCombos();
        LoadValues();
    }

    private void UpdateLabelsAndPreview()
    {
        DockConfig config = _config.Current;
        DockLayoutConfig d = config.Dock;
        AppearanceConfig a = config.Appearance;

        IconSizeValue.Text = Loc.Instance.T("Settings_ValuePx", d.IconSize);
        IconSpacingValue.Text = Loc.Instance.T("Settings_ValuePx", d.IconSpacing);
        PaddingValue.Text = Loc.Instance.T("Settings_ValuePx", d.Padding);
        CornerValue.Text = Loc.Instance.T("Settings_ValuePx", d.CornerRadius);
        OffsetValue.Text = Loc.Instance.T("Settings_ValuePx", d.OffsetFromEdge);
        OpacityValue.Text = Loc.Instance.T("Settings_ValuePercent", (int)Math.Round(a.Opacity * 100));
        ReflectionStrengthValue.Text = Loc.Instance.T("Settings_ValuePercent", (int)Math.Round(a.Reflection.Strength * 100));
        MagnifyValue.Text = Loc.Instance.T("Settings_ValueTimes", config.Animation.MagnifyScale.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture));

        byte alpha = (byte)Math.Round(Math.Clamp(a.Opacity, 0, 1) * 255);
        PreviewCapsule.Background = new SolidColorBrush(Color.FromArgb(alpha, 28, 30, 38));
        PreviewCapsule.CornerRadius = new CornerRadius(Math.Min(d.CornerRadius, 24));

        PreviewIcons.Children.Clear();
        double size = Math.Clamp(d.IconSize * 0.6, 20, 58);
        double radius = d.IconShape switch
        {
            "circle" => size / 2,
            "rect" => 0,
            _ => Math.Max(2, size * 0.17),
        };
        for (int i = 0; i < 3; i++)
        {
            PreviewIcons.Children.Add(new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(Color.FromArgb(
                    90, (byte)(120 + (i * 35)), 150, (byte)(220 - (i * 30)))),
                Margin = new Thickness(d.IconSpacing / 4.0, 0,
                    d.IconSpacing / 4.0, 0),
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

    /// <summary>导出当前配置（WD-031 / 6.14）。</summary>
    private void ExportConfig()
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.Instance.T("Settings_ExportTitle"),
            Filter = Loc.Instance.T("Settings_JsonFilter"),
            FileName = "windock-config.json",
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _saveTimer?.Dispose();
            _config.ExportTo(dialog.FileName);
            MessageBox.Show(this, Loc.Instance.T("Settings_ExportDone"),
                Loc.Instance.T("Settings_ExportTitle"), MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _log.Error(Module, "导出配置失败", ex);
            MessageBox.Show(this, ex.Message, Loc.Instance.T("Settings_ExportTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>从文件导入配置并即时生效（WD-031 / 6.14）。</summary>
    private void ImportConfig()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Instance.T("Settings_ImportTitle"),
            Filter = Loc.Instance.T("Settings_JsonFilter"),
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _saveTimer?.Dispose();
            _config.ImportFrom(dialog.FileName);
            Loc.Instance.SetLanguage(_config.Current.General.Language);
            ApplyTexts();
            BuildCombos();
            LoadValues();
            MessageBox.Show(this, Loc.Instance.T("Settings_ImportDone"),
                Loc.Instance.T("Settings_ImportTitle"), MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
                                       or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _log.Warn(Module, $"导入配置失败：{ex.Message}");
            MessageBox.Show(this, Loc.Instance.T("Settings_ImportFailed"),
                Loc.Instance.T("Settings_ImportTitle"), MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>检查更新（WD-028）：离线版本无更新服务器，按「已是最新」提示，不联网。</summary>
    private void CheckUpdate()
    {
        MessageBox.Show(this, Loc.Instance.T("Settings_UpdateUpToDate"),
            Loc.Instance.T("Settings_UpdateCheck"), MessageBoxButton.OK,
            MessageBoxImage.Information);
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
