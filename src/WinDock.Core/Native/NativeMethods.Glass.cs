using System.Runtime.InteropServices;

namespace WinDock.Core.Native;

/// <summary>
/// 「自绘毛玻璃」所需的 GDI 抓屏与截屏排除声明（质感改造）。
/// </summary>
/// <remarks>
/// 为什么不直接用 DWM 的 Acrylic/Mica：<c>DWMWA_SYSTEMBACKDROP_TYPE</c> 作用于
/// **整个窗口矩形**，而这个 Dock 窗口比胶囊大一圈（要给图标放大留溢出空间）。
/// 一旦打开系统 backdrop，胶囊外侧会露出一整块矩形雾，比不加还难看。
/// 因此改为「抓胶囊背后的桌面 → 高斯模糊 → 当作胶囊背景」，模糊只落在胶囊形状内，
/// 图标放大溢出也完全不受影响。抓屏用 <c>CreateDIBSection + BitBlt</c>，
/// 不引入 System.Drawing.Common（项目保持零 NuGet 依赖）。
/// </remarks>
public static partial class NativeMethods
{
    /// <summary>窗口参与截屏（默认）。</summary>
    public const uint WDA_NONE = 0x00000000;

    /// <summary>把窗口从截屏/BitBlt/屏幕捕获中排除（Win10 2004+）。</summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public const uint SRCCOPY = 0x00CC0020;

    /// <summary>同时抓取分层窗口（否则半透明窗口背后的内容会缺失）。</summary>
    public const uint CAPTUREBLT = 0x40000000;

    public const uint DIB_RGB_COLORS = 0x00000000;

    public const uint BI_RGB = 0x00000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>设置窗口的截屏可见性；返回 false 表示系统不支持（老版本）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int cx, int cy,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    /// <summary>等待 DWM 提交一帧；用于「改完显示亲和性、抓屏前」确保改动已进合成链。</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    public const uint DESKTOP_READOBJECTS = 0x0001;

    /// <summary>
    /// 打开当前输入桌面。锁屏 / UAC 安全桌面下会失败 —— 此时任何抓屏都拿不到
    /// 用户桌面，也没有必要重试。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(IntPtr hDesktop);
}
