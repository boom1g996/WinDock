using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinDock.Core.Interfaces;

namespace WinDock.Core.Services;

/// <summary>
/// 图标磁盘缓存（WD-018 / FR-6.11）：缓存目录 %LocalAppData%\WinDock\IconCache\，
/// key = 路径 + 文件修改时间 + 目标尺寸，取 SHA-256 前 16 字节作文件名。
/// mtime 变化会生成新 key，旧条目自然失效，符合「应用更新后自动失效重建」。
/// 加载用 CacheOption.OnLoad 避免长期占用文件句柄，BitmapSource 冻结后可跨线程共享。
/// </summary>
public sealed class IconCache : IIconCache
{
    private const string Module = "IconCache";
    private const int RetentionDays = 30;

    private readonly ILogService _log;
    private readonly string _directory;
    private readonly object _sync = new();
    private readonly Dictionary<string, ImageSource> _memory = new(StringComparer.Ordinal);

    /// <param name="log">日志服务。</param>
    /// <param name="directory">可选自定义目录；离线自检用临时目录，避免污染真实缓存。</param>
    public IconCache(ILogService log, string? directory = null)
    {
        _log = log;
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinDock", "IconCache");
        Directory.CreateDirectory(_directory);
        Prune();
    }

    /// <summary>缓存目录绝对路径，便于自检与日志核对。</summary>
    public string DirectoryPath => _directory;

    public ImageSource? Load(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_sync)
        {
            if (_memory.TryGetValue(key, out ImageSource? hit))
            {
                return hit;
            }
        }

        string path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            lock (_sync)
            {
                _memory[key] = image;
            }

            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or InvalidOperationException)
        {
            _log.Warn(Module, $"缓存读取失败，将重新提取：{Path.GetFileName(path)}；{ex.Message}");
            TryDelete(path);
            return null;
        }
    }

    public void Store(string key, ImageSource icon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(icon);
        if (icon is not BitmapSource bitmap)
        {
            return;
        }

        lock (_sync)
        {
            _memory[key] = icon;
        }

        string path = PathFor(key);
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or InvalidOperationException)
        {
            // 缓存是加速手段，写失败绝不能影响图标显示；下次启动会重新提取。
            _log.Warn(Module, $"缓存写入失败，已忽略：{ex.Message}");
            TryDelete(path);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _memory.Clear();
        }

        foreach (string file in SafeFiles())
        {
            TryDelete(file);
        }
    }

    /// <summary>删除超过保留期的缓存文件，避免长期累积（不阻塞启动，失败只记日志）。</summary>
    private void Prune()
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        int removed = 0;
        foreach (string file in SafeFiles())
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (IOException)
            {
            }
        }

        if (removed > 0)
        {
            _log.Info(Module, $"清理过期图标缓存 {removed} 个");
        }
    }

    private IEnumerable<string> SafeFiles()
    {
        try
        {
            return Directory.EnumerateFiles(_directory, "*.png").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(Module, $"缓存目录不可读：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    private string PathFor(string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_directory, Convert.ToHexString(hash.AsSpan(0, 16)) + ".png");
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(Module, $"缓存文件删除失败：{ex.Message}");
        }
    }
}
