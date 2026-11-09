using System.Globalization;
using System.Resources;

namespace WinDock.Core;

/// <summary>
/// 字符串资源入口（需求 8.5 / 设计决策 D8）：所有 UI 文案从 .resx 读取，
/// 代码与 XAML 中不硬编码中英文。XAML 可用 Path=[Key] 绑定索引器。
/// </summary>
public sealed class Loc
{
    private static readonly Lazy<Loc> Singleton = new(() => new Loc());

    private readonly ResourceManager _manager = new(
        "WinDock.Core.Resources.Strings", typeof(Loc).Assembly);

    private Loc()
    {
    }

    /// <summary>供 XAML 绑定的单例。</summary>
    public static Loc Instance => Singleton.Value;

    /// <summary>索引器，支持 {Binding Path=[Menu_Settings]}。</summary>
    public string this[string key] => T(key);

    /// <summary>取本地化字符串；缺键时回退为键名，便于定位遗漏。</summary>
    public string T(string key)
    {
        try
        {
            return _manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
        catch (MissingSatelliteAssemblyException)
        {
            return _manager.GetString(key, CultureInfo.InvariantCulture) ?? key;
        }
    }

    /// <summary>带格式参数的本地化字符串。</summary>
    public string T(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>按配置切换界面语言（v1 支持 zh-CN 与 en）。</summary>
    public void SetLanguage(string language)
    {
        CultureInfo culture = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? new CultureInfo("en")
            : new CultureInfo("zh-CN");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
