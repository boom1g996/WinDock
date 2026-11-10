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
    private const string DefaultSpec = "Ctrl+Alt+D";

    private readonly ILogService _log;
    private IntPtr _hwnd;
    private bool _enabled = true;
    private bool _registered;
    private string _spec = DefaultSpec;

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
        string next = string.IsNullOrWhiteSpace(config.Behavior.HotkeyToggle)
            ? DefaultSpec
            : config.Behavior.HotkeyToggle.Trim();
        if (!string.Equals(next, _spec, StringComparison.OrdinalIgnoreCase))
        {
            // 6.10 快捷键可配置：换组合键必须注销旧注册再重注册，
            // 否则 RegisterHotKey 会以「已注册」直接返回成功但仍是旧键。
            _spec = next;
            Unregister();
        }

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

        if (!TryParseSpec(_spec, out uint modifiers, out uint virtualKey))
        {
            _log.Warn(Module, $"快捷键配置无法解析：{_spec}，回退 {DefaultSpec}");
            _spec = DefaultSpec;
            if (!TryParseSpec(_spec, out modifiers, out virtualKey))
            {
                return;
            }
        }

        if (!RegisterHotKey(_hwnd, HotkeyId, modifiers | MOD_NOREPEAT, virtualKey))
        {
            int error = Marshal.GetLastWin32Error();
            _log.Warn(Module, $"{_spec} 注册失败 Win32Error={error}（可能被其他程序占用），快捷键降级为不可用");
            return;
        }

        _registered = true;
        _log.Info(Module, $"全局快捷键 {_spec} 已注册");
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

    /// <summary>
    /// 解析 "Ctrl+Alt+D" / "Ctrl+Shift+D" / "Ctrl+Alt+Space" 形式的快捷键。
    /// 只支持单字符键、空格与 F1~F24；解析失败返回 false 由调用方回退默认值。
    /// </summary>
    private static bool TryParseSpec(string spec, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        string[] parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i];
            bool last = i == parts.Length - 1;
            if (string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_CONTROL;
            }
            else if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_ALT;
            }
            else if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_SHIFT;
            }
            else if (string.Equals(token, "Win", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_WIN;
            }
            else if (last)
            {
                if (string.Equals(token, "Space", StringComparison.OrdinalIgnoreCase))
                {
                    virtualKey = 0x20;
                }
                else if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
                {
                    virtualKey = char.ToUpperInvariant(token[0]);
                }
                else if (token.Length is >= 2 and <= 3
                         && (token[0] is 'F' or 'f')
                         && int.TryParse(token[1..], out int fn)
                         && fn is >= 1 and <= 24)
                {
                    virtualKey = (uint)(0x70 + fn - 1);
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return virtualKey != 0;
    }
}
