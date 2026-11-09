using System.IO;
using System.Runtime.InteropServices;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Native;
using static WinDock.Core.Native.NativeMethods;

namespace WinDock.Core.Services;

/// <summary>启动、激活、关闭窗口（FR-301 / AC-04）。前台锁失败时重试 3 次。</summary>
public sealed class AppLauncher : IAppLauncher
{
    private const string Module = "Launcher";

    private readonly ILogService _log;
    private readonly IMetricsService? _metrics;

    public AppLauncher(ILogService log, IMetricsService? metrics = null)
    {
        _log = log;
        _metrics = metrics;
    }

    public bool Launch(PinnedItem item)
    {
        if (item.Type == "uwp")
        {
            if (string.IsNullOrWhiteSpace(item.Aumid))
            {
                _log.Warn(Module, "UWP 固定项缺少 AUMID");
                return false;
            }

            return ShellExecute("explorer.exe", $"shell:AppsFolder\\{item.Aumid}");
        }

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            _log.Warn(Module, "固定项缺少可执行路径");
            return false;
        }

        return ShellExecute(item.Path, item.Args);
    }

    public bool NewWindow(PinnedItem item)
    {
        if (item.Type != "app" || string.IsNullOrWhiteSpace(item.Path))
        {
            return Launch(item);
        }

        string args = item.Args;
        if (string.IsNullOrWhiteSpace(args))
        {
            string name = Path.GetFileNameWithoutExtension(item.Path).ToLowerInvariant();
            if (name.Contains("chrome", StringComparison.Ordinal)
                || name.Contains("msedge", StringComparison.Ordinal)
                || name.Contains("firefox", StringComparison.Ordinal))
            {
                args = "--new-window";
            }
        }

        // M1 降级：不模拟 Ctrl+N，也不做「应用是否支持多窗口」探测；
        // 除常见浏览器外等价于再次启动，由应用自身决定是否复用进程。
        return ShellExecute(item.Path, args);
    }

    public bool Activate(AppWindowGroup group)
    {
        AppWindowInfo? target = group.Windows.FirstOrDefault(w => w.IsForeground)
                                ?? group.Windows.FirstOrDefault(w => !w.IsMinimized)
                                ?? group.Windows.FirstOrDefault();
        return target is not null && Activate(target);
    }

    public bool Activate(AppWindowInfo window)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return ActivateCore(window);
        }
        finally
        {
            _metrics?.Record("launcher.activate", sw.Elapsed.TotalMilliseconds); // AC-21 点击激活时延
        }
    }

    private bool ActivateCore(AppWindowInfo window)
    {
        IntPtr handle = window.Handle;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ShowWindow(handle, window.IsMinimized ? SW_RESTORE : SW_SHOWNORMAL);
            BringWindowToTop(handle);
            if (SetForegroundWindow(handle) && GetForegroundWindow() == handle)
            {
                _log.Debug(Module, $"激活成功（第 {attempt} 次）：{window.Title}");
                return true;
            }

            // R3：前台锁定，用 AttachThreadInput 临时挂接前台线程输入队列后重试
            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = foreground == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foreground, out _);
            uint selfThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0
                            && foregroundThread != selfThread
                            && AttachThreadInput(selfThread, foregroundThread, true);
            try
            {
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
                if (GetForegroundWindow() == handle)
                {
                    _log.Debug(Module, $"AttachThreadInput 激活成功（第 {attempt} 次）");
                    return true;
                }
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(selfThread, foregroundThread, false);
                }
            }

            Thread.Sleep(15);
        }

        _log.Warn(Module, $"三次激活均失败：{window.Title}（{window.Handle}）");
        return false;
    }

    public void CloseAll(AppWindowGroup group)
    {
        foreach (AppWindowInfo window in group.Windows.ToList())
        {
            PostMessage(window.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        _log.Info(Module, $"已发送 WM_CLOSE ×{group.Windows.Count}：{group.DisplayName}");
    }

    private bool ShellExecute(string file, string args)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_FLAG_NO_UI, // 失败时不弹系统错误框
            hwnd = IntPtr.Zero,
            lpVerb = "open",
            lpFile = file,
            lpParameters = args ?? "",
            lpDirectory = Path.GetDirectoryName(file) ?? "",
            nShow = SW_SHOWNORMAL,
            lpClass = "",
        };

        bool ok = ShellExecuteEx(ref info);
        if (!ok)
        {
            _log.Error(Module, $"ShellExecuteEx 失败：{file}，Win32Error={Marshal.GetLastWin32Error()}");
        }
        else
        {
            _log.Info(Module, $"启动：{file} {args}");
        }

        return ok;
    }
}
