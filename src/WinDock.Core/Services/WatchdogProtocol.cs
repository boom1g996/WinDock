using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using WinDock.Core.Models;

namespace WinDock.Core.Services;

/// <summary>一次看门狗恢复事件；模式 C 下任务栏本就未被隐藏，用于验证检测链路。</summary>
public sealed class WatchdogRecoveryEvent
{
    public int Pid { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset Utc { get; set; }
    public bool TaskbarRestored { get; set; }
    public bool SnapshotPresent { get; set; }
}

/// <summary>
/// 主进程与 WinDock.Watchdog.exe 之间的共享契约（WD-020 / D8）。
/// 只使用 .NET 内置的命名管道与 UTF-8 单行帧，不注入、不提权、不注册服务。
/// 帧：H = 心跳，S = 正常退出；管道断开 / 超时 / 宿主消失均判为异常。
/// </summary>
public static class WatchdogProtocol
{
    public const string HeartbeatFrame = "H";
    public const string ShutdownFrame = "S";
    public const string PipePrefix = "WinDock.Watchdog.v1.";
    public const string RecoveryFileName = "watchdog-recovery.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>按宿主 PID 命名，避免多实例串线；重启后会使用新管道名。</summary>
    public static string PipeName(int pid) =>
        PipePrefix + pid.ToString(CultureInfo.InvariantCulture);

    public static string RecoveryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinDock", RecoveryFileName);

    public static int ClampTimeout(int milliseconds) => Math.Clamp(
        milliseconds,
        ConfigRanges.WatchdogHeartbeatMs.Min,
        ConfigRanges.WatchdogHeartbeatMs.Max);

    /// <summary>心跳间隔取失联阈值的 1/3，保证三次机会内必有数据。</summary>
    public static int HeartbeatIntervalMs(int timeoutMs) =>
        Math.Clamp(ClampTimeout(timeoutMs) / 3, 300, 2000);

    /// <summary>宿主 PID 存活检查周期：进程被强杀时远快于 10s 即可恢复（4.4 要求 5s）。</summary>
    public static int PollIntervalMs => 500;

    public static bool IsHeartbeat(string? frame) =>
        string.Equals(frame, HeartbeatFrame, StringComparison.Ordinal);

    public static bool IsShutdown(string? frame) =>
        string.Equals(frame, ShutdownFrame, StringComparison.Ordinal);

    public static WatchdogRecoveryEvent Create(int pid, string reason,
        bool taskbarRestored, bool snapshotPresent) => new()
        {
            Pid = pid,
            Reason = reason,
            Utc = DateTimeOffset.UtcNow,
            TaskbarRestored = taskbarRestored,
            SnapshotPresent = snapshotPresent,
        };

    public static string Serialize(WatchdogRecoveryEvent value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    public static WatchdogRecoveryEvent? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WatchdogRecoveryEvent>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>原子写恢复事件；下次主进程启动读取后由托盘气泡提示并删除。</summary>
    public static void WriteRecovery(WatchdogRecoveryEvent value)
    {
        string path = RecoveryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(value), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public static WatchdogRecoveryEvent? ReadRecovery()
    {
        try
        {
            return File.Exists(RecoveryPath)
                ? Deserialize(File.ReadAllText(RecoveryPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void ClearRecovery()
    {
        try
        {
            if (File.Exists(RecoveryPath))
            {
                File.Delete(RecoveryPath);
            }
        }
        catch (IOException)
        {
        }
    }
}
