using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WinDock.Core.Interfaces;

namespace WinDock.Core.Services;

/// <summary>本地文件日志：[模块][级别] 时间 消息；用户名脱敏；保留 7 天。</summary>
public sealed class LogService : ILogService
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _currentFile;

    private static readonly Regex UserPathPattern = new(
        @"[A-Za-z]:\\Users\\([^\\/]+)\\",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public LogService()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinDock", "logs");
        Directory.CreateDirectory(_directory);
        _currentFile = Path.Combine(_directory, $"windock-{DateTime.Now:yyyyMMdd}.log");
        PurgeExpiredLogs();
    }

    /// <summary>日志目录，供设置窗口「打开日志目录」使用。</summary>
    public string DirectoryPath => _directory;

    public void Debug(string module, string message) => Write("DEBUG", module, message);
    public void Info(string module, string message) => Write("INFO", module, message);
    public void Warn(string module, string message) => Write("WARN", module, message);

    public void Error(string module, string message, Exception? exception = null)
    {
        string text = exception is null
            ? message
            : $"{message}{Environment.NewLine}{exception}";
        Write("ERROR", module, text);
    }

    private void Write(string level, string module, string message)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string line = $"[{module}][{level}] {stamp} {Scrub(message)}{Environment.NewLine}";
        try
        {
            lock (_sync)
            {
                File.AppendAllText(_currentFile, line, new UTF8Encoding(false));
            }
        }
        catch (IOException)
        {
            // 日志写入失败不能影响主流程（需求 8.2：禁止弹系统崩溃框）
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Scrub(string message) =>
        UserPathPattern.Replace(message,
            m => m.Value.Replace(m.Groups[1].Value, "***", StringComparison.Ordinal));

    private void PurgeExpiredLogs()
    {
        DateTime cutoff = DateTime.Now.AddDays(-7);
        try
        {
            foreach (string file in Directory.EnumerateFiles(_directory, "windock-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
        catch (IOException)
        {
        }
    }
}
