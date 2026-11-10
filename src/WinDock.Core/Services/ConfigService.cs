using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;

namespace WinDock.Core.Services;

/// <summary>
/// 配置服务：原子写、schemaVersion、越界夹紧、损坏回退并备份、跨进程热更新。
/// 配置真源是磁盘文件，因此设置窗口是独立进程也能在 200ms 内让主进程生效。
/// </summary>
public sealed class ConfigService : IConfigService
{
    private const string Module = "Config";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly ILogService _log;
    private readonly FileSystemWatcher _watcher;
    private readonly object _sync = new();
    private Timer? _debounce;
    private int _reloadGuard;
    private DockConfig _current;
    private string _lastSerialized = "";
    // 已广播内容：同一份配置内容绝不重复触发 Changed，切断「写盘 → 监视器 → 重载 → 写盘」
    // 的自激振荡（2026-11-10 实机曾出现持续 20 分钟的热重载风暴，主线程被 ApplyConfig 占满）。
    private string _lastBroadcast = "";

    /// <param name="log">日志服务。</param>
    /// <param name="configDirectory">可选自定义目录；离线自检用临时目录，避免污染真实配置。</param>
    public ConfigService(ILogService log, string? configDirectory = null)
    {
        _log = log;
        string directory = configDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinDock");
        Directory.CreateDirectory(directory);
        ConfigPath = Path.Combine(directory, "config.json");

        _current = LoadFromDisk(out List<string> warnings);
        _lastSerialized = Serialize(_current);
        _lastBroadcast = _lastSerialized;
        foreach (string warning in warnings)
        {
            _log.Warn(Module, warning);
        }

        _watcher = new FileSystemWatcher(directory, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
    }

    public string ConfigPath { get; }

    public DockConfig Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    /// <summary>夹紧所有越界值、补齐缺失子对象、修正非法枚举，并返回警告列表。</summary>
    /// <remarks>静态方法便于离线自检直接调用，不依赖文件系统。</remarks>
    public static void Normalize(DockConfig config, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(warnings);

        if (config.SchemaVersion < ConfigRanges.MinSchemaVersion)
        {
            warnings.Add($"schemaVersion={config.SchemaVersion} 非法，重置为 1");
            config.SchemaVersion = 1;
        }
        else if (config.SchemaVersion > ConfigRanges.MinSchemaVersion)
        {
            warnings.Add($"schemaVersion={config.SchemaVersion} 高于当前支持的 1，未知字段已忽略");
        }

        config.General ??= new GeneralConfig();
        config.Dock ??= new DockLayoutConfig();
        config.Appearance ??= new AppearanceConfig();
        config.Appearance.Reflection ??= new ReflectionConfig();
        config.Animation ??= new AnimationConfig();
        config.Behavior ??= new BehaviorConfig();
        config.Dock.MonitorIds ??= new List<string> { "primary" };
        config.Pinned ??= new List<PinnedItem>();
        config.Pinned.RemoveAll(p => p is null);

        var g = config.General;
        g.TaskbarMode = Choice(g.TaskbarMode, ConfigRanges.TaskbarModes, "beautify", "general.taskbarMode", warnings);
        g.Language = Choice(g.Language, ConfigRanges.Languages, "zh-CN", "general.language", warnings);

        var d = config.Dock;
        d.Position = Choice(d.Position, ConfigRanges.Positions, "bottom", "dock.position", warnings);
        d.IconShape = Choice(d.IconShape, ConfigRanges.IconShapes, "roundedRect", "dock.iconShape", warnings);
        d.IconSize = Clamp(d.IconSize, ConfigRanges.IconSize, "dock.iconSize", warnings);
        d.IconSpacing = Clamp(d.IconSpacing, ConfigRanges.IconSpacing, "dock.iconSpacing", warnings);
        d.Padding = Clamp(d.Padding, ConfigRanges.Padding, "dock.padding", warnings);
        d.CornerRadius = Clamp(d.CornerRadius, ConfigRanges.CornerRadius, "dock.cornerRadius", warnings);
        d.OffsetFromEdge = Clamp(d.OffsetFromEdge, ConfigRanges.OffsetFromEdge, "dock.offsetFromEdge", warnings);
        if (d.MonitorIds.Count == 0)
        {
            d.MonitorIds.Add("primary");
        }

        var a = config.Appearance;
        a.Preset = Choice(a.Preset, ConfigRanges.Presets, "custom", "appearance.preset", warnings);
        a.BackgroundMode = Choice(a.BackgroundMode, ConfigRanges.BackgroundModes, "acrylic", "appearance.backgroundMode", warnings);
        a.BlurLevel = Choice(a.BlurLevel, ConfigRanges.BlurLevels, "medium", "appearance.blurLevel", warnings);
        a.ShadowLevel = Choice(a.ShadowLevel, ConfigRanges.ShadowLevels, "medium", "appearance.shadowLevel", warnings);
        a.Opacity = Clamp(a.Opacity, ConfigRanges.Opacity, "appearance.opacity", warnings);
        a.BorderWidth = Clamp(a.BorderWidth, ConfigRanges.BorderWidth, "appearance.borderWidth", warnings);
        if (string.IsNullOrWhiteSpace(a.BorderColor))
        {
            a.BorderColor = "#1FFFFFFF";
        }

        a.AccentColor ??= "";

        a.Reflection.Strength = Clamp(a.Reflection.Strength, ConfigRanges.ReflectionStrength, "appearance.reflection.strength", warnings);
        a.Reflection.Height = Clamp(a.Reflection.Height, ConfigRanges.ReflectionHeight, "appearance.reflection.height", warnings);

        var anim = config.Animation;
        anim.Decay = Choice(anim.Decay, ConfigRanges.DecayModes, "cosine", "animation.decay", warnings);
        anim.MagnifyScale = Clamp(anim.MagnifyScale, ConfigRanges.MagnifyScale, "animation.magnifyScale", warnings);
        anim.MagnifyRadius = Clamp(anim.MagnifyRadius, ConfigRanges.MagnifyRadius, "animation.magnifyRadius", warnings);
        anim.MagnifyDurationMs = Clamp(anim.MagnifyDurationMs, ConfigRanges.MagnifyDurationMs, "animation.magnifyDurationMs", warnings);
        anim.SpeedMultiplier = Clamp(anim.SpeedMultiplier, ConfigRanges.SpeedMultiplier, "animation.speedMultiplier", warnings);
        anim.BounceCount = Clamp(anim.BounceCount, ConfigRanges.BounceCount, "animation.bounceCount", warnings);
        anim.IndicatorFadeMs = Clamp(anim.IndicatorFadeMs, ConfigRanges.IndicatorFadeMs, "animation.indicatorFadeMs", warnings);
        anim.IndicatorStyle = Choice(anim.IndicatorStyle, ConfigRanges.IndicatorStyles, "dot", "animation.indicatorStyle", warnings);

        var b = config.Behavior;
        b.ClickActiveApp = Choice(b.ClickActiveApp, ConfigRanges.ClickActiveAppModes, "showWindows", "behavior.clickActiveApp", warnings);
        b.RunningSort = Choice(b.RunningSort, ConfigRanges.RunningSorts, "recent", "behavior.runningSort", warnings);
        b.HideDelayMs = Clamp(b.HideDelayMs, ConfigRanges.HideDelayMs, "behavior.hideDelayMs", warnings);
        b.EdgeHotzonePx = Clamp(b.EdgeHotzonePx, ConfigRanges.EdgeHotzonePx, "behavior.edgeHotzonePx", warnings);
        b.PreviewHoverDelayMs = Clamp(b.PreviewHoverDelayMs, ConfigRanges.PreviewHoverDelayMs, "behavior.previewHoverDelayMs", warnings);
        b.PreviewMaxWindows = Clamp(b.PreviewMaxWindows, ConfigRanges.PreviewMaxWindows, "behavior.previewMaxWindows", warnings);
        b.WatchdogHeartbeatMs = Clamp(b.WatchdogHeartbeatMs, ConfigRanges.WatchdogHeartbeatMs, "behavior.watchdogHeartbeatMs", warnings);
        b.IconLabelMode = Choice(b.IconLabelMode, ConfigRanges.IconLabelModes, "tooltip", "behavior.iconLabelMode", warnings);
        if (string.IsNullOrWhiteSpace(b.HotkeyToggle))
        {
            b.HotkeyToggle = "Ctrl+Alt+D";
        }

        foreach (PinnedItem item in config.Pinned)
        {
            item.Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id;
            item.Type ??= "app";
            item.Path ??= "";
            item.Args ??= "";
            item.Aumid ??= "";
            item.Name ??= "";
        }
    }

    public void Save(DockConfig config)
    {
        var warnings = new List<string>();
        Normalize(config, warnings);
        string json = Serialize(config);

        lock (_sync)
        {
            if (json == _lastSerialized)
            {
                return;
            }

            WriteAtomic(json);
            _lastSerialized = json;
            _current = config;
        }

        // 自激振荡防护：同一内容不重复广播。
        if (json == _lastBroadcast)
        {
            return;
        }

        _lastBroadcast = json;
        foreach (string warning in warnings)
        {
            _log.Warn(Module, warning);
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(config, warnings));
    }

    public void Reload()
    {
        DockConfig config;
        List<string> warnings;
        string json;

        lock (_sync)
        {
            config = LoadFromDisk(out warnings);
            json = Serialize(config);
            if (json == _lastSerialized)
            {
                return;
            }

            _lastSerialized = json;
            _current = config;
        }

        if (json == _lastBroadcast)
        {
            return;
        }

        _lastBroadcast = json;
        foreach (string warning in warnings)
        {
            _log.Warn(Module, warning);
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(config, warnings));
    }

    /// <inheritdoc />
    public void ExportTo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json;
        lock (_sync)
        {
            json = Serialize(_current);
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, json, new UTF8Encoding(false));
        _log.Info(Module, $"已导出配置到 {path}");
    }

    /// <inheritdoc />
    public DockConfig ImportFrom(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json = File.ReadAllText(path, Encoding.UTF8);
        DockConfig config;
        try
        {
            config = JsonSerializer.Deserialize<DockConfig>(json, JsonOptions)
                     ?? throw new InvalidDataException("配置文件为空");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("配置文件不是合法 JSON", ex);
        }

        var warnings = new List<string>();
        Normalize(config, warnings);
        foreach (string warning in warnings)
        {
            _log.Warn(Module, warning);
        }

        // 复用 Save：原子写 + 去重广播，导入后主进程 200ms 内热更新。
        Save(config);
        _log.Info(Module, $"已从 {path} 导入配置");
        return config;
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce?.Dispose();
    }

    private void ScheduleReload()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref _reloadGuard, 1) != 0)
            {
                return;
            }

            try
            {
                Reload();
            }
            catch (Exception ex)
            {
                _log.Error(Module, "热更新重载失败", ex);
            }
            finally
            {
                Volatile.Write(ref _reloadGuard, 0);
            }
        }, null, 120, Timeout.Infinite);
    }

    private DockConfig LoadFromDisk(out List<string> warnings)
    {
        warnings = new List<string>();
        if (!File.Exists(ConfigPath))
        {
            DockConfig fresh = DockConfig.CreateDefault();
            WriteAtomic(Serialize(fresh));
            _log.Info(Module, "首次运行，已写入默认配置");
            return fresh;
        }

        try
        {
            string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
            DockConfig config = JsonSerializer.Deserialize<DockConfig>(json, JsonOptions)
                                ?? DockConfig.CreateDefault();
            Normalize(config, warnings);
            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            BackupCorruptFile();
            warnings.Add("配置文件损坏或不可读，已回退默认配置并备份原文件");
            _log.Error(Module, "配置损坏，回退默认配置", ex);
            DockConfig fresh = DockConfig.CreateDefault();
            WriteAtomic(Serialize(fresh));
            return fresh;
        }
    }

    private void BackupCorruptFile()
    {
        try
        {
            string backup = Path.Combine(
                Path.GetDirectoryName(ConfigPath) ?? ".",
                $"config.corrupt.{DateTime.Now:yyyyMMddHHmmss}.json");
            File.Copy(ConfigPath, backup, overwrite: true);
            _log.Warn(Module, $"已备份损坏配置：{backup}");
        }
        catch (IOException)
        {
        }
    }

    private void WriteAtomic(string json)
    {
        string temp = ConfigPath + ".tmp";
        string backup = ConfigPath + ".bak";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        try
        {
            if (File.Exists(ConfigPath))
            {
                File.Replace(temp, ConfigPath, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, ConfigPath, overwrite: true);
            }
        }
        catch (IOException)
        {
            // 文件被短暂占用时降级为覆盖写；仍然先写完临时文件，绝不写半截 JSON。
            File.Copy(temp, ConfigPath, overwrite: true);
            File.Delete(temp);
        }
    }

    private static string Serialize(DockConfig config) =>
        JsonSerializer.Serialize(config, JsonOptions);

    private static int Clamp(int value, (int Min, int Max) range, string name, ICollection<string> warnings)
    {
        int result = Math.Clamp(value, range.Min, range.Max);
        if (result != value)
        {
            warnings.Add($"{name}={value} 越界，夹紧为 {result}（{range.Min}~{range.Max}）");
        }

        return result;
    }

    private static double Clamp(double value, (double Min, double Max) range, string name, ICollection<string> warnings)
    {
        double result = double.IsNaN(value) ? range.Min : Math.Clamp(value, range.Min, range.Max);
        if (Math.Abs(result - value) > 0.000001 || double.IsNaN(value))
        {
            warnings.Add($"{name}={value} 越界，夹紧为 {result}（{range.Min}~{range.Max}）");
        }

        return result;
    }

    private static string Choice(string? value, string[] allowed, string fallback,
        string name, ICollection<string> warnings)
    {
        if (!string.IsNullOrEmpty(value) && allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return allowed.First(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        }

        warnings.Add($"{name}={value ?? "null"} 非法，回退为 {fallback}");
        return fallback;
    }
}
