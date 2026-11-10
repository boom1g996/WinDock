using System.Runtime.InteropServices;

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
}
