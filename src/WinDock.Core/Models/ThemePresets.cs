namespace WinDock.Core.Models;

/// <summary>
/// 内置预设主题（WD-023 / 6.10）：把一套外观 + 布局 + 动效参数一次性写入配置。
/// 预设只是「参数批量赋值」，不引入任何渲染分支——渲染仍只读 DockConfig，
/// 因此用户可以「应用预设后再微调」（应用后 Preset 置为该预设名，微调则置 custom）。
/// </summary>
public static class ThemePresets
{
    /// <summary>全部预设标识（与 ConfigRanges.Presets 保持一致，custom 除外）。</summary>
    public static readonly string[] Names =
    {
        "macosLight", "macosDark", "mono", "cyber", "glass",
    };

    /// <summary>把指定预设应用到配置上（就地修改）。未知名称按 macOS 深色处理。</summary>
    public static void Apply(DockConfig config, string preset)
    {
        ArgumentNullException.ThrowIfNull(config);
        AppearanceConfig a = config.Appearance ??= new AppearanceConfig();
        DockLayoutConfig d = config.Dock ??= new DockLayoutConfig();
        AnimationConfig anim = config.Animation ??= new AnimationConfig();

        // 预设只改「外观 / 布局 / 动效」，不动 Behavior（自动隐藏、预览等用户偏好）
        // 与 General（开机自启、语言），避免应用主题时把用户行为设置冲掉。
        switch (preset)
        {
            case "macosLight":
                a.BackgroundMode = "acrylic";
                a.Opacity = 0.55;
                a.BlurLevel = "high";
                a.BorderColor = "#33FFFFFF";
                a.BorderWidth = 1;
                a.ShadowLevel = "medium";
                a.AccentColor = "#FF0A84FF"; // macOS 系统蓝
                a.Reflection.Enabled = true;
                a.Reflection.Strength = 0.35;
                d.IconShape = "roundedRect";
                d.CornerRadius = 22;
                d.IconSize = 48;
                d.IconSpacing = 10;
                anim.MagnifyScale = 1.75;
                anim.MagnifyRadius = 2.5;
                break;

            case "macosDark":
                a.BackgroundMode = "acrylic";
                a.Opacity = 0.30;
                a.BlurLevel = "high";
                a.BorderColor = "#1FFFFFFF";
                a.BorderWidth = 1;
                a.ShadowLevel = "medium";
                a.AccentColor = "#FF0A84FF";
                a.Reflection.Enabled = true;
                a.Reflection.Strength = 0.4;
                d.IconShape = "roundedRect";
                d.CornerRadius = 22;
                d.IconSize = 48;
                d.IconSpacing = 10;
                anim.MagnifyScale = 1.75;
                anim.MagnifyRadius = 2.5;
                break;

            case "mono":
                a.BackgroundMode = "solid";
                a.Opacity = 0.85;
                a.BlurLevel = "low";
                a.BorderColor = "#40FFFFFF";
                a.BorderWidth = 1;
                a.ShadowLevel = "weak";
                a.AccentColor = "#FFFFFFFF";
                a.Reflection.Enabled = false;
                d.IconShape = "rect";
                d.CornerRadius = 8;
                d.IconSize = 44;
                d.IconSpacing = 8;
                anim.MagnifyScale = 1.5;
                anim.MagnifyRadius = 2.0;
                break;

            case "cyber":
                a.BackgroundMode = "acrylic";
                a.Opacity = 0.45;
                a.BlurLevel = "high";
                a.BorderColor = "#66FF00E5"; // 品红描边
                a.BorderWidth = 1.5;
                a.ShadowLevel = "strong";
                a.AccentColor = "#FF00E5FF"; // 青色强调
                a.Reflection.Enabled = true;
                a.Reflection.Strength = 0.6;
                d.IconShape = "circle";
                d.CornerRadius = 24;
                d.IconSize = 52;
                d.IconSpacing = 12;
                anim.MagnifyScale = 2.0;
                anim.MagnifyRadius = 3.0;
                break;

            case "glass":
            default:
                a.BackgroundMode = "acrylic";
                a.Opacity = 0.15;
                a.BlurLevel = "high";
                a.BorderColor = "#26FFFFFF";
                a.BorderWidth = 1;
                a.ShadowLevel = "medium";
                a.AccentColor = "#FF64D2FF";
                a.Reflection.Enabled = true;
                a.Reflection.Strength = 0.25;
                d.IconShape = "roundedRect";
                d.CornerRadius = 26;
                d.IconSize = 48;
                d.IconSpacing = 10;
                anim.MagnifyScale = 1.75;
                anim.MagnifyRadius = 2.5;
                break;
        }

        a.Preset = string.Equals(preset, "custom", StringComparison.OrdinalIgnoreCase)
            ? "custom"
            : preset;
    }
}
