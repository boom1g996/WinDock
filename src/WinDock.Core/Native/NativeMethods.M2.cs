using System.Runtime.InteropServices;

namespace WinDock.Core.Native;

/// <summary>
/// M2 新增 P/Invoke 与常量，与 M1 同属 <see cref="NativeMethods"/> 类型，保证
/// 「所有非托管声明集中可审计」（需求 14.4 / M2 设计决策 D9）。
/// 每一项都由 tools/probe_m2.py 在 Build 26200 上实测导出存在，且有可回滚的调用验证。
/// 约定：一律 SetLastError=true；HRESULT 失败由调用方 Marshal.GetLastWin32Error() 辅助诊断。
/// </summary>
public static partial class NativeMethods
{
    public const uint EVENT_SYSTEM_ALERT = 0x0002;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B; // F11/全屏切换会 resize 窗口
    public const uint EVENT_OBJECT_STATECHANGE = 0x800A;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint WM_HOTKEY = 0x0312;

    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    public const int QUNS_PRESENTATION_MODE = 4;
    public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;

    public const uint WS_EX_TOOLWINDOW_FLAG = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint LWA_ALPHA = 0x00000002;

    public const uint FLASHW_ALL = 0x00000003;
    public const uint FLASHW_TIMERNOFG = 0x0000000C;

    public const uint DWM_TNP_RECTDESTINATION = 0x00000001;
    public const uint DWM_TNP_OPACITY = 0x00000004;
    public const uint DWM_TNP_VISIBLE = 0x00000008;
    public const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    public const uint DWM_FRAME_DURATION_DEFAULT = 0;

    // 热区窗口（WD-015 / D5）：2px 原生分层窗口，不使用全局鼠标钩子。
    public const uint WS_POPUP = 0x80000000;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int HWND_TOPMOST = -1;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>RegisterClassExW 的窗口类描述（Unicode）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    /// <summary>原生窗口过程；委托实例必须保活，避免被 GC 回收导致回调崩溃。</summary>
    public delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName,
        string lpWindowName, uint dwStyle, int x, int y, int width, int height,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg,
        IntPtr wParam, IntPtr lParam);

    /// <summary>DWM 缩略图源尺寸（DwmQueryThumbnailSourceSize 输出）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    /// <summary>DwmUpdateThumbnailProperties 的属性集，字段顺序与 SDK 头文件一致。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    /// <summary>EnumDisplayMonitors 回调；参数用 IntPtr 避免 null RECT 的 ref 语义问题。</summary>
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
        IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("dwmapi.dll", SetLastError = true)]
    public static extern int DwmRegisterThumbnail(IntPtr hwndDestination,
        IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll", SetLastError = true)]
    public static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll", SetLastError = true)]
    public static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnailId,
        out SIZE pSize);

    [DllImport("dwmapi.dll", SetLastError = true)]
    public static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId,
        ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id,
        uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool FlashWindowEx(ref FLASHWINFO pfwi);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd,
        uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);

    // ---- 托盘（WD-034：去 WinForms 化，纯 Shell_NotifyIcon） -----------------

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_INFO = 0x00000010;
    public const uint NIIF_WARNING = 0x00000003;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_BOTTOMALIGN = 0x0020;

    public const uint IMAGE_ICON = 1;
    public const uint LR_SHARED = 0x00008000;
    public const uint LR_DEFAULTSIZE = 0x00000040;

    /// <summary>Shell_NotifyIconW 的数据结构（Unicode 版本，字段序与 SDK 头一致）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    /// <summary>
    /// 共享加载系统图标。lpIconName 用 IntPtr：序数资源（IDI_*）必须传
    /// MAKEINTRESOURCE(id)（即数值指针 &lt; 0x10000），字符串封送会被当成
    /// 资源名查找并报 1813（M2 第五批实测）。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImageW(IntPtr hInstance, IntPtr lpIconName,
        uint uType, int cxDesired, int cyDesired, uint fuLoad);

    /// <summary>IDI_APPLICATION = 32512（0x7F00）。</summary>
    public const int IDI_APPLICATION = 32512;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem,
        string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags,
        int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    /// <summary>把一个 HRESULT 转成便于日志阅读的十六进制串。</summary>
    public static string HResultText(int hr) =>
        hr == 0 ? "S_OK" : $"0x{ unchecked((uint)hr):X8}";
}
