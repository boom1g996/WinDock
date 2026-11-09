using System.Runtime.InteropServices;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using static WinDock.Core.Native.NativeMethods;

namespace WinDock.Core.Services;

/// <summary>
/// 全局快捷键 Ctrl+Alt+D（WD-015 / FR-602 / 已确认默认值 4）：
/// 只用 RegisterHotKey + WM_HOTKEY，可卸载，不安装全局键盘或鼠标钩子。
/// 消息由 MainWindow.WndProc 转发给 IAutoHideController.Toggle()。
/// </summary>
public sealed class HotkeyService : IHotkeyService
{
    private const string Module = "Hotkey";
    private const int HotkeyId = 0x5A4D;
    private const uint Modifiers = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
    private const uint VirtualKeyD = 0x44;

    private readonly ILogService _log;
    private IntPtr _hwnd;
    private bool _enabled = true;
    private bool _registered;

    public HotkeyService(ILogService log)
    {
        _log = log;
    }

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Apply();
    }

    public void ApplyConfig(DockConfig config)
    {
        _enabled = config.Behavior.HotkeyEnabled;
        Apply();
    }

    public void Dispose()
    {
        Unregister();
        _hwnd = IntPtr.Zero;
    }

    private void Apply()
    {
        if (!_enabled || _hwnd == IntPtr.Zero)
        {
            Unregister();
            return;
        }

        if (_registered)
        {
            return;
        }

        if (!RegisterHotKey(_hwnd, HotkeyId, Modifiers, VirtualKeyD))
        {
            int error = Marshal.GetLastWin32Error();
            _log.Warn(Module, $"Ctrl+Alt+D 注册失败 Win32Error={error}（可能被其他程序占用），快捷键降级为不可用");
            return;
        }

        _registered = true;
        _log.Info(Module, "全局快捷键 Ctrl+Alt+D 已注册");
    }

    private void Unregister()
    {
        if (!_registered || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!UnregisterHotKey(_hwnd, HotkeyId))
        {
            int error = Marshal.GetLastWin32Error();
            // 退出时 HWND 可能已销毁（ERROR_INVALID_WINDOW_HANDLE=1400），
            // 系统会随窗口销毁自动释放热键登记，不算故障。
            if (error != 1400)
            {
                _log.Warn(Module, $"注销快捷键失败 Win32Error={error}");
            }
        }

        _registered = false;
    }
}
