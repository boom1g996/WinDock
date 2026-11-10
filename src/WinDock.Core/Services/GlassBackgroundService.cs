using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using WinDock.Core.Interfaces;
using WinDock.Core.Native;

namespace WinDock.Core.Services;

/// <summary>
/// 胶囊背景的「自绘毛玻璃」：抓取 Dock 背后的屏幕画面 → 高斯模糊 → 交给 Border 当背景。
/// </summary>
/// <remarks>
/// <para>
/// 与 DWM 系统 backdrop 的分工：<c>DWMWA_SYSTEMBACKDROP_TYPE</c> 只能作用于整个窗口矩形，
/// 而本窗口为了容纳图标放大溢出，比胶囊大一圈（左右各 ~28px、上方 ~48px）。用系统
/// backdrop 会在胶囊外侧露出一整块矩形雾。改为自绘后模糊严格落在胶囊形状内，
/// 放大溢出、倒影、阴影都不受影响。
/// </para>
/// <para>
/// 抓屏用 <c>CreateDIBSection + BitBlt</c>，不依赖 System.Drawing.Common（保持零 NuGet）。
/// 抓屏期间用 <c>WDA_EXCLUDEFROMCAPTURE</c> 把 Dock 自身排除，否则会抓到自己形成
/// 无限镜像；若系统不支持该亲和性则直接停用玻璃（回退半透明纯色），不做"自拍"。
/// </para>
/// <para>
/// 结果只存在于内存，不落盘、不外发（需求 8.6）。
/// </para>
/// </remarks>
public sealed class GlassBackgroundService
{
    private const string Module = "Glass";

    /// <summary>单次抓屏的像素上限，防止异常尺寸拖死 GDI。</summary>
    private const long MaxPixels = 8_000_000;

    private readonly ILogService _log;
    private bool _captureUnavailable;
    private int _failures;
    private string _cacheKey = "";
    private BitmapSource? _cache;

    public GlassBackgroundService(ILogService log)
    {
        _log = log;
    }

    /// <summary>玻璃是否还能用（抓屏被排除亲和性挡住时为 false，调用方应回退纯色）。</summary>
    public bool IsAvailable => !_captureUnavailable;

    /// <summary>配置变更 / 壁纸变更后丢弃缓存，下次 <see cref="Render"/> 重新抓。</summary>
    public void Invalidate()
    {
        _cacheKey = "";
        _cache = null;
    }

    /// <summary>
    /// 抓取屏幕矩形并模糊。坐标均为**物理像素**（调用方负责换算 DPI）。
    /// </summary>
    /// <param name="owner">Dock 窗口句柄，用于抓屏期间把自身排除。</param>
    /// <param name="x">矩形左上角屏幕 X（物理像素）。</param>
    /// <param name="y">矩形左上角屏幕 Y（物理像素）。</param>
    /// <param name="width">宽度（物理像素）。</param>
    /// <param name="height">高度（物理像素）。</param>
    /// <param name="blurRadius">高斯模糊半径（物理像素）。</param>
    /// <returns>模糊后的位图；失败返回 null，调用方回退纯色。</returns>
    public BitmapSource? Render(IntPtr owner, int x, int y, int width, int height, double blurRadius)
    {
        if (_captureUnavailable || owner == IntPtr.Zero)
        {
            return null;
        }

        // 锁屏 / UAC 安全桌面下抓屏注定失败，且原因是环境不是方案 ——
        // 不计入失败次数，用户解锁后会自动恢复（不能因此永久停用玻璃）。
        if (!IsInputDesktopAccessible())
        {
            _failures = 0;
            return null;
        }

        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
        {
            return null;
        }

        string key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{x}|{y}|{width}|{height}|{blurRadius:F1}");
        if (string.Equals(key, _cacheKey, StringComparison.Ordinal) && _cache is not null)
        {
            return _cache;
        }

        BitmapSource? raw = CaptureScreen(owner, x, y, width, height);
        if (raw is null)
        {
            // 连续失败通常是全屏独占（游戏/D3D）或安全桌面：停用玻璃，
            // 不再每次位置变化都重试抓屏（那会持续占用 CPU 且注定失败）。
            if (++_failures >= 3)
            {
                _captureUnavailable = true;
                _log.Warn(Module, "抓屏连续失败 3 次，玻璃背景停用（胶囊回退半透明纯色）");
            }

            return null;
        }

        _failures = 0;

        BitmapSource blurred = Blur(raw, blurRadius);
        _cache = blurred;
        _cacheKey = key;
        return blurred;
    }

    /// <summary>
    /// 抓屏；抓屏期间把 owner 在 **HWND 层**临时隐藏，结束后恢复。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 实机验证过、都不可行的三条路（别再走回头路）：
    /// </para>
    /// <list type="number">
    /// <item><c>WDA_EXCLUDEFROMCAPTURE</c>：设置后**同进程**的 <c>BitBlt</c> 直接
    /// <c>ERROR_ACCESS_DENIED(5)</c>，Win10 2004+ 的截屏保护是进程级的。</item>
    /// <item><c>DWMWA_CLOAKED=1</c>：只读属性，写入返回 <c>E_INVALIDARG</c>。</item>
    /// <item><c>PrintWindow(GetDesktopWindow())</c>：返回 0 且内容全黑，拿不到壁纸。</item>
    /// <item>临时加 <c>WS_EX_LAYERED</c> + <c>SetLayeredWindowAttributes(alpha=0)</c>：
    /// WPF 的 HwndSource 自己接管窗口样式，<c>SetWindowLong</c> 改不动，
    /// 随后 <c>SetLayeredWindowAttributes</c> 报 <c>ERROR_INVALID_PARAMETER(87)</c>。</item>
    /// </list>
    /// <para>
    /// 最终用 <c>ShowWindow(SW_HIDE)</c>：它是 HWND 级隐藏，**不动 WPF 的
    /// <c>Visibility</c>**，所以不会触发 K-22 的 <c>SizeToContent</c> 2×2 收缩与重排；
    /// 恢复用 <c>SW_SHOWNOACTIVATE</c>，避免抢走用户当前窗口的焦点。
    /// 窗口本来就隐藏（自动隐藏态）时什么都不做，直接抓。
    /// </para>
    /// </remarks>
    private BitmapSource? CaptureScreen(IntPtr owner, int x, int y, int width, int height)
    {
        bool visible = NativeMethods.IsWindowVisible(owner);
        try
        {
            if (visible)
            {
                NativeMethods.ShowWindow(owner, NativeMethods.SW_HIDE);
                NativeMethods.DwmFlush(); // 等这一帧真的不再画自己
            }

            return BitBltScreen(x, y, width, height);
        }
        finally
        {
            if (visible)
            {
                NativeMethods.ShowWindow(owner, NativeMethods.SW_SHOWNOACTIVATE);
                NativeMethods.DwmFlush();
            }
        }
    }

    /// <summary>当前是否处于可交互的用户桌面（锁屏 / UAC 安全桌面返回 false）。</summary>
    private static bool IsInputDesktopAccessible()
    {
        IntPtr desktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.CloseDesktop(desktop);
        return true;
    }

    private BitmapSource? BitBltScreen(int x, int y, int width, int height)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            _log.Warn(Module, $"GetDC(桌面) 失败 Win32Error={Marshal.GetLastWin32Error()}");
            return null;
        }

        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr oldObject = IntPtr.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                return null;
            }

            var info = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // 负高度 = 自上而下，与 BitmapSource 行序一致
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                },
            };

            dib = NativeMethods.CreateDIBSection(screenDc, ref info,
                NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                _log.Warn(Module, $"CreateDIBSection 失败 Win32Error={Marshal.GetLastWin32Error()}");
                return null;
            }

            oldObject = NativeMethods.SelectObject(memDc, dib);

            // CAPTUREBLT 让半透明窗口背后的内容一起进画面。
            if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, x, y,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                // 全屏独占 / 安全桌面（UAC）下会失败，属正常回退路径。
                _log.Info(Module, $"BitBlt 抓屏失败 Win32Error={Marshal.GetLastWin32Error()}");
                return null;
            }

            int stride = width * 4;
            var buffer = new byte[stride * height];
            Marshal.Copy(bits, buffer, 0, buffer.Length);

            // DIB 的 alpha 通道未定义，用 Bgr32 交给 WPF（模糊后会输出不透明 Pbgra32）。
            BitmapSource source = BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Bgr32, null, buffer, stride);
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ExternalException
                                       or ArgumentException)
        {
            _log.Error(Module, $"抓屏异常（{width}×{height}）", ex);
            return null;
        }
        finally
        {
            if (oldObject != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memDc, oldObject);
            }

            if (dib != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(dib);
            }

            if (memDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// 高斯模糊。用 <c>RenderingBias.Performance</c>：模糊半径较大时质量档会走
    /// 更重的多次采样，而这里是"本来就要糊"的背景，性能档肉眼无差。
    /// </summary>
    private static BitmapSource Blur(BitmapSource source, double radius)
    {
        var visual = new DrawingVisual();
        double r = Math.Clamp(radius, 0, 96);
        if (r > 0.5)
        {
            visual.Effect = new BlurEffect
            {
                Radius = r,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance,
            };
        }

        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Linear);
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }

        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight,
            96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
