using System.Runtime.InteropServices;
using System.Text;

namespace WinDock.Core.Native;

/// <summary>
/// M3 新增 P/Invoke（WD-024 系统区）：回收站查询/清空、Win 键组合发送。
/// 与 M1/M2 同属 <see cref="NativeMethods"/> partial 类型，保持「非托管声明集中可审计」。
/// 一律 SetLastError=true；失败由调用方用 Marshal.GetLastWin32Error() 记录。
/// </summary>
public static partial class NativeMethods
{
    // ---- 回收站（shell32）----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    public const uint SHERB_NOCONFIRMATION = 0x00000001;
    public const uint SHERB_NOPROGRESSUI = 0x00000002;
    public const uint SHERB_NOSOUND = 0x00000004;

    /// <summary>显示设置变化（分辨率 / 缩放 / 显示器热插拔），WD-021 重新布局。</summary>
    public const int WM_DISPLAYCHANGE = 0x007E;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    /// <summary>回收站是否为空；查询失败按「非空」处理，避免误判为空而隐藏清空入口。</summary>
    public static bool IsRecycleBinEmpty()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        int hr = SHQueryRecycleBin(null, ref info);
        return hr == 0 && info.i64NumItems == 0;
    }

    /// <summary>清空回收站（无确认框 / 无进度 UI / 无声音，由调用方先做二次确认）。</summary>
    public static int EmptyRecycleBin()
    {
        return SHEmptyRecycleBin(IntPtr.Zero, null,
            SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
    }

    // ---- Win 键组合（user32）----

    public const byte VK_LWIN = 0x5B;
    public const byte VK_TAB = 0x09;
    public const byte VK_D = 0x44;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>发送一次 Win + key（如 Win+Tab 任务视图、Win+D 显示桌面）。</summary>
    public static void SendWinKey(byte key)
    {
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ---- 材质与兼容性（6.10 材质组 / 8.3 兼容性矩阵）----

    /// <summary>远程桌面会话（RDP）：需求 8.3 要求禁用毛玻璃、回退纯色。</summary>
    public const int SM_REMOTESESSION = 0x1000;

    // DWM 系统背景类型常量已在 NativeMethods.cs（M1）声明：DWMWA_SYSTEMBACKDROP_TYPE /
    // DWMSBT_NONE / DWMSBT_MAINWINDOW / DWMSBT_TRANSIENTWINDOW，此处不重复定义。

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetColorizationColor(out uint pcrColorization, out int pfOpaqueBlend);

    /// <summary>当前是否为远程桌面会话（拉取型检测，不注册任何会话通知）。</summary>
    public static bool IsRemoteSession() => GetSystemMetrics(SM_REMOTESESSION) != 0;

    /// <summary>DWM 合成是否启用；禁用时（部分虚拟机）毛玻璃不可用，必须回退纯色。</summary>
    public static bool IsDwmCompositionEnabled()
    {
        return DwmIsCompositionEnabled(out int enabled) == 0 && enabled != 0;
    }

    /// <summary>
    /// 系统强调色（6.10 颜色组默认值「系统强调色」）：取 DWM 着色色，
    /// 失败回退 macOS 系统蓝。返回值恒为不透明色。
    /// </summary>
    public static uint GetSystemAccentArgb()
    {
        if (DwmGetColorizationColor(out uint argb, out _) == 0)
        {
            // DwmGetColorizationColor 返回 0xAARRGGBB；丢弃过低的有效位（近黑视为不可用）
            uint rgb = argb & 0x00FFFFFF;
            byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
            if (r + g + b > 24)
            {
                return 0xFF000000u | rgb;
            }
        }

        return 0xFF0A84FFu;
    }

    // ---- ShellHook（FR-203 应用请求注意）----

    /// <summary>shell 广播「某窗口请求闪烁/注意」事件号（RegisterShellHookWindow 后的 wParam）。</summary>
    public const int HSHELL_FLASH = 0x8006;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterShellHookWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DeregisterShellHookWindow(IntPtr hwnd);

    // ---- 注册表（6.13 模式 B「精简共存」：隐藏搜索/任务视图/小组件）----

    public static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));
    public static readonly IntPtr HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));
    public const int KEY_READ = 0x20019;
    public const int KEY_WRITE = 0x20006;
    public const int REG_DWORD = 4;
    public const int REG_SZ = 1;
    public const int REG_EXPAND_SZ = 2;
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_NO_MORE_ITEMS = 259;

    public const int WM_SETTINGCHANGE = 0x001A;
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    /// <summary>按窗口类名查找（6.13：检测系统托盘溢出面板是否被打开）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegOpenKeyEx(IntPtr hKey, string lpSubKey, int ulOptions,
        int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegQueryValueEx(IntPtr hKey, string lpValueName, IntPtr lpReserved,
        out int lpType, byte[]? lpData, ref int lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegSetValueEx(IntPtr hKey, string lpValueName, int reserved,
        int dwType, byte[] lpData, int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegEnumKeyEx(IntPtr hKey, int dwIndex, StringBuilder lpName,
        ref int lpcchName, IntPtr lpReserved, IntPtr lpClass, IntPtr lpcchClass,
        IntPtr lpftLastWriteTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegCloseKey(IntPtr hKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam,
        string lParam, int flags, int timeout, out IntPtr result);

    /// <summary>写一个 HKCU 下的 DWORD（不存在则创建）。返回 Win32 错误码，0 表示成功。</summary>
    public static int WriteDword(string subKey, string valueName, int value)
    {
        int error = RegOpenKeyEx(HKEY_CURRENT_USER, subKey, 0, KEY_READ | KEY_WRITE, out IntPtr key);
        if (error != ERROR_SUCCESS)
        {
            return error;
        }

        try
        {
            byte[] data = BitConverter.GetBytes(value);
            return RegSetValueEx(key, valueName, 0, REG_DWORD, data, data.Length);
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    /// <summary>读一个 HKCU 下的 DWORD；不存在或非 DWORD 时返回 null。</summary>
    public static int? ReadDword(string subKey, string valueName)
    {
        int error = RegOpenKeyEx(HKEY_CURRENT_USER, subKey, 0, KEY_READ, out IntPtr key);
        if (error != ERROR_SUCCESS)
        {
            return null;
        }

        try
        {
            var buffer = new byte[4];
            int size = buffer.Length;
            error = RegQueryValueEx(key, valueName, IntPtr.Zero, out int type, buffer, ref size);
            if (error != ERROR_SUCCESS || type != REG_DWORD || size < 4)
            {
                return null;
            }

            return BitConverter.ToInt32(buffer, 0);
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    /// <summary>
    /// 读字符串值（REG_SZ / REG_EXPAND_SZ）。供 R9 冲突检测扫描卸载项名称，
    /// 失败返回 null 而不是抛异常 —— 检测本身不允许影响启动。
    /// </summary>
    public static string? ReadString(IntPtr root, string subKey, string valueName)
    {
        int error = RegOpenKeyEx(root, subKey, 0, KEY_READ, out IntPtr key);
        if (error != ERROR_SUCCESS)
        {
            return null;
        }

        try
        {
            int size = 0;
            error = RegQueryValueEx(key, valueName, IntPtr.Zero, out int type, null, ref size);
            if (error != ERROR_SUCCESS || size <= 0 || (type != REG_SZ && type != REG_EXPAND_SZ))
            {
                return null;
            }

            var buffer = new byte[size];
            error = RegQueryValueEx(key, valueName, IntPtr.Zero, out type, buffer, ref size);
            if (error != ERROR_SUCCESS)
            {
                return null;
            }

            string text = Encoding.Unicode.GetString(buffer, 0, size);
            int nul = text.IndexOf('\0');
            return nul >= 0 ? text[..nul] : text;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    /// <summary>枚举子键名（一级）。用于遍历卸载注册表项；不存在时返回空集合。</summary>
    public static IReadOnlyList<string> SubKeyNames(IntPtr root, string subKey)
    {
        var names = new List<string>();
        int error = RegOpenKeyEx(root, subKey, 0, KEY_READ, out IntPtr key);
        if (error != ERROR_SUCCESS)
        {
            return names;
        }

        try
        {
            var buffer = new StringBuilder(512);
            for (int index = 0; index < 4096; index++)
            {
                int length = buffer.Capacity;
                error = RegEnumKeyEx(key, index, buffer, ref length, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero);
                if (error == ERROR_NO_MORE_ITEMS)
                {
                    break;
                }

                if (error != ERROR_SUCCESS)
                {
                    break;
                }

                names.Add(buffer.ToString(0, length));
            }
        }
        finally
        {
            RegCloseKey(key);
        }

        return names;
    }

    /// <summary>通知 shell 重新读取任务栏设置（探索器收到后即时刷新，无需重启）。</summary>
    public static void BroadcastTraySettings()
    {
        _ = SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero,
            "TraySettings", 0x0002 /*SMTO_ABORTIFHUNG*/, 1000, out _);
    }
}
