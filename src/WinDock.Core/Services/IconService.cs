using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Native;
using static WinDock.Core.Native.NativeMethods;

namespace WinDock.Core.Services;

/// <summary>
/// 图标提取：优先按目标像素尺寸从 exe 内嵌资源取高清图标，逐级回退，
/// 最终回退为自绘问号图标。M1 只做内存缓存，磁盘缓存留到 M2（WD-018）。
/// </summary>
public sealed class IconService : IIconService
{
    private const string Module = "Icon";

    private readonly ILogService _log;
    private readonly IIconCache? _diskCache;
    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Stamp, DateTime CachedUtc)> _stampCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _privateExtractAvailable;

    /// <param name="log">日志服务。</param>
    /// <param name="diskCache">M2 磁盘缓存；传 null 时退化为 M1 的纯内存缓存（便于自检）。</param>
    public IconService(ILogService log, IIconCache? diskCache = null)
    {
        _log = log;
        _diskCache = diskCache;
        _privateExtractAvailable = HasExport("user32.dll", "PrivateExtractIconsW");
        _log.Info(Module, $"PrivateExtractIconsW 导出可用={_privateExtractAvailable}；磁盘缓存启用={_diskCache is not null}");
    }

    public ImageSource? GetIcon(PinnedItem item, int size)
    {
        if (item.Type == "separator")
        {
            return null;
        }

        if (item.Type == "uwp")
        {
            // 6.11：UWP 通过 AUMID 解析为 shell 命名空间项（shell:AppsFolder\<AUMID>），
            // 由 shell 自己给出磁贴图标；解析失败仍回退问号占位（明确降级，不假装成功）。
            if (!string.IsNullOrWhiteSpace(item.Aumid))
            {
                ImageSource? uwp = GetUwpIcon(item.Aumid, size);
                if (uwp is not null)
                {
                    return uwp;
                }
            }

            _log.Warn(Module, $"UWP 图标解析失败，使用占位图标：{item.Aumid}");
            return QuestionMark(size);
        }

        return GetIcon(item.Path, size);
    }

    public ImageSource? GetIcon(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return QuestionMark(size);
        }

        string key = CacheKey(path, size);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out ImageSource? cached))
            {
                return cached;
            }
        }

        // M2 WD-018：内存未命中先查磁盘缓存，避免每次冷启动都做图标提取。
        ImageSource? fromDisk = _diskCache?.Load(key);
        if (fromDisk is not null)
        {
            lock (_cache)
            {
                _cache[key] = fromDisk;
            }

            return fromDisk;
        }

        bool exists = File.Exists(path) || Directory.Exists(path);
        ImageSource result;
        try
        {
            result = exists
                ? Extract(path, size)
                : QuestionMark(size);
            if (!exists)
            {
                _log.Warn(Module, $"路径失效，使用问号图标：{path}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DllNotFoundException or EntryPointNotFoundException
                                       or System.ComponentModel.Win32Exception)
        {
            _log.Error(Module, $"图标提取失败：{path}", ex);
            result = QuestionMark(size);
            exists = false;
        }

        lock (_cache)
        {
            _cache[key] = result;
        }

        // 只缓存真实提取到的图标；问号占位不入磁盘，避免失效路径长期占空间。
        if (exists)
        {
            _diskCache?.Store(key, result);
        }

        return result;
    }

    /// <summary>
    /// UWP 图标：把 "shell:AppsFolder\&lt;AUMID&gt;" 解析成 PIDL 后交给 SHGetFileInfo。
    /// shell 会返回该应用的磁贴图标（32px 档），高 DPI 下略糊，属已知限制。
    /// </summary>
    private ImageSource? GetUwpIcon(string aumid, int size)
    {
        string key = $"uwp:{aumid}:{size}";
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out ImageSource? cached))
            {
                return cached;
            }
        }

        string parseName = aumid.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            ? aumid
            : "shell:AppsFolder\\" + aumid;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(parseName, IntPtr.Zero, out pidl, 0, out _) != 0
                || pidl == IntPtr.Zero)
            {
                return null;
            }

            SHFILEINFO info = default;
            uint infoSize = (uint)Marshal.SizeOf<SHFILEINFO>();
            if (SHGetFileInfo(pidl.ToString(), 0, ref info, infoSize,
                    SHGFI_ICON | SHGFI_PIDL | SHGFI_LARGEICON) == IntPtr.Zero
                || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                ImageSource image = FromHIcon(info.hIcon);
                lock (_cache)
                {
                    _cache[key] = image;
                }

                return image;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or System.ComponentModel.Win32Exception)
        {
            _log.Warn(Module, $"UWP 图标解析异常：{ex.Message}");
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                ILFree(pidl);
            }
        }
    }

    public void Invalidate(string path)
    {
        lock (_cache)
        {
            foreach (string key in _cache.Keys.Where(k => k.StartsWith(path, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _cache.Remove(key);
            }
        }
    }

    private ImageSource Extract(string path, int size)
    {
        if (_privateExtractAvailable)
        {
            ImageSource? highDpi = TryPrivateExtract(path, size);
            if (highDpi is not null)
            {
                return highDpi;
            }
        }

        return TryExtractIconEx(path)
               ?? TryShellIcon(path)
               ?? QuestionMark(size);
    }

    private static ImageSource? TryPrivateExtract(string path, int size)
    {
        var handles = new IntPtr[1];
        var ids = new uint[1];
        int count = PrivateExtractIcons(path, 0, size, size, handles, ids, 1, 0);
        if (count <= 0 || handles[0] == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return FromHIcon(handles[0]);
        }
        finally
        {
            DestroyIcon(handles[0]);
        }
    }

    private static ImageSource? TryExtractIconEx(string path)
    {
        if (ExtractIconEx(path, 0, out IntPtr large, out IntPtr small, 1) == 0)
        {
            return null;
        }

        IntPtr chosen = large != IntPtr.Zero ? large : small;
        if (chosen == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return FromHIcon(chosen);
        }
        finally
        {
            if (large != IntPtr.Zero)
            {
                DestroyIcon(large);
            }

            if (small != IntPtr.Zero)
            {
                DestroyIcon(small);
            }
        }
    }

    private static ImageSource? TryShellIcon(string path)
    {
        SHFILEINFO info = default;
        uint size = (uint)Marshal.SizeOf<SHFILEINFO>();
        if (SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info, size, SHGFI_ICON) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return FromHIcon(info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static ImageSource FromHIcon(IntPtr handle)
    {
        BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
            handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze(); // 冻结后可在 UI 线程外安全共享
        return source;
    }

    private static ImageSource QuestionMark(int size)
    {
        int pixel = Math.Clamp(size, 16, 256);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(230, 88, 94, 108)),
                new Pen(new SolidColorBrush(Color.FromArgb(255, 160, 166, 180)), 1),
                new Rect(0, 0, pixel, pixel), pixel * 0.2, pixel * 0.2);

            var text = new FormattedText(
                "?", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), pixel * 0.55,
                Brushes.White, 1.25);
            context.DrawText(text, new Point((pixel - text.Width) / 2, (pixel - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(pixel, pixel, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 缓存键含文件时间戳，但时间戳本身也缓存 30s：每次 Dock 数据刷新都为
    /// 每个固定项做一次 GetLastWriteTimeUtc 会造成可观的磁盘 IO 与空闲 CPU。
    /// </summary>
    private string CacheKey(string path, int size)
    {
        string stamp;
        lock (_stampCache)
        {
            if (_stampCache.TryGetValue(path, out var entry)
                && (DateTime.UtcNow - entry.CachedUtc).TotalSeconds < 30)
            {
                stamp = entry.Stamp;
            }
            else
            {
                stamp = ResolveStamp(path);
                _stampCache[path] = (stamp, DateTime.UtcNow);
            }
        }

        return $"{path}|{stamp}|{size}";
    }

    private static string ResolveStamp(string path)
    {
        try
        {
            return File.Exists(path)
                ? File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture)
                : "missing";
        }
        catch (IOException)
        {
            return "unknown";
        }
    }
}
