using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Native;
using static WinDock.Core.Native.NativeMethods;

namespace WinDock.Core.Services;

/// <summary>
/// 事件驱动的窗口枚举（设计决策 D6）：SetWinEventHook + 启动全量扫描，无轮询。
/// 事件洪泛时用 SynchronizationContext 合并为一次刷新，避免每个窗口事件触发枚举。
/// </summary>
public sealed class WindowEnumerator : IWindowEnumerator
{
    private const string Module = "WindowEnum";

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "Windows.UI.Core.CoreWindow", "Shell_SecondaryTrayWnd_Old",
    };

    private readonly ILogService _log;
    private readonly SynchronizationContext? _uiContext;
    private readonly List<WinEventProc> _hookProcs = new();
    private readonly List<IntPtr> _hooks = new();
    private readonly object _sync = new();
    private readonly Dictionary<uint, ProcessSnapshot> _processCache = new();
    private Timer? _debounce;
    private int _refreshPending;
    private IReadOnlyList<AppWindowGroup> _groups = Array.Empty<AppWindowGroup>();
    private string _lastSignature = "";
    private bool _running;

    public WindowEnumerator(ILogService log, SynchronizationContext? uiContext = null)
    {
        _log = log;
        _uiContext = uiContext ?? SynchronizationContext.Current;
    }

    public event EventHandler? WindowsChanged;

    public IReadOnlyList<AppWindowGroup> Groups
    {
        get
        {
            lock (_sync)
            {
                return _groups;
            }
        }
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        // 窄区间注册：0x0003~0x0017 整段包含 MOVE/CAPTURE/SWITCH 等高频事件，
        // 实测事件洪泛会把空闲 CPU 推到 30%+，因此只订阅真正需要的三段。
        AddHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        AddHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        AddHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE);
        _debounce = new Timer(_ => DispatchRefresh(), null, Timeout.Infinite, Timeout.Infinite);
        _running = true;
        Refresh();
        _log.Info(Module, $"事件钩子就绪，初始聚合 {Groups.Count} 个应用");
    }

    public void Stop()
    {
        foreach (IntPtr hook in _hooks)
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWinEvent(hook);
            }
        }

        _hooks.Clear();
        _hookProcs.Clear();
        _debounce?.Dispose();
        _debounce = null;
        _running = false;
    }

    public void Dispose() => Stop();

    private sealed record ProcessSnapshot(
        string ExecutablePath, string DisplayName, DateTime CachedUtc);

    private void AddHook(uint min, uint max)
    {
        WinEventProc proc = OnWinEvent;
        _hookProcs.Add(proc); // 保活：Win32 回调委托被 GC 回收会导致进程崩溃
        IntPtr hook = SetWinEventHook(min, max, IntPtr.Zero, proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook == IntPtr.Zero)
        {
            _log.Error(Module, $"SetWinEventHook({min:X}~{max:X}) 失败：{Marshal.GetLastWin32Error()}");
        }
        else
        {
            _hooks.Add(hook);
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero)
        {
            return;
        }

        ScheduleRefresh();
    }

    /// <summary>事件去抖：250ms 内的窗口事件合并为一次刷新（不是轮询）。</summary>
    private void ScheduleRefresh()
    {
        _debounce?.Change(250, Timeout.Infinite);
    }

    private void DispatchRefresh()
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) != 0)
        {
            return;
        }

        Action work = () =>
        {
            try
            {
                if (Refresh())
                {
                    WindowsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                _log.Error(Module, "窗口刷新失败", ex);
            }
            finally
            {
                Volatile.Write(ref _refreshPending, 0);
            }
        };

        if (_uiContext is not null)
        {
            _uiContext.Post(_ => work(), null);
        }
        else
        {
            ThreadPool.QueueUserWorkItem(_ => work());
        }
    }

    /// <returns>窗口集合是否真的变化；无变化时 UI 不应重建。</returns>
    private bool Refresh()
    {
        IntPtr foreground = GetForegroundWindow();
        uint ownPid = (uint)Environment.ProcessId;
        var byKey = new Dictionary<string, AppWindowGroup>(StringComparer.OrdinalIgnoreCase);
        var titleBuffer = new StringBuilder(512);
        var classBuffer = new StringBuilder(256);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            // DWMWA_CLOAKED 过滤是 M0 实测的必需项（419 个顶层窗口中 12 个被 cloaked）
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                && cloaked != 0)
            {
                return true;
            }

            long exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }

            classBuffer.Clear();
            GetClassName(hwnd, classBuffer, classBuffer.Capacity);
            string className = classBuffer.ToString();
            if (ShellClasses.Contains(className))
            {
                return true;
            }

            titleBuffer.Clear();
            GetWindowText(hwnd, titleBuffer, titleBuffer.Capacity);
            string title = titleBuffer.ToString();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == ownPid)
            {
                return true;
            }

            string executable = ResolveExecutable(pid, out string cachedName);
            string key = string.IsNullOrEmpty(executable) ? className : executable;
            if (!byKey.TryGetValue(key, out AppWindowGroup? group))
            {
                group = new AppWindowGroup
                {
                    Key = key,
                    ExecutablePath = executable,
                    DisplayName = string.IsNullOrEmpty(cachedName) ? title : cachedName,
                };
                byKey.Add(key, group);
            }

            group.Windows.Add(new AppWindowInfo
            {
                Handle = hwnd,
                Title = title,
                ProcessId = (int)pid,
                ExecutablePath = executable,
                ClassName = className,
                IsForeground = hwnd == foreground,
                IsMinimized = IsIconic(hwnd),
            });
            if (hwnd == foreground)
            {
                group.LastActiveUtc = DateTime.UtcNow;
            }

            return true;
        }, IntPtr.Zero);

        List<AppWindowGroup> result = byKey.Values
            .OrderByDescending(g => g.Windows.Any(w => w.IsForeground))
            .ThenBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        lock (_sync)
        {
            _groups = result;
        }

        // 签名只描述「Dock 结构 + 运行状态」，绝不能包含窗口标题：浏览器 / IM /
        // 编辑器标题变化极频繁，纳入签名会让每次变化都整条 Dock 重建。
        // Groups 里的 Title 每次仍会刷新（供右键窗口列表使用），只是不触发 UI 重建。
        string signature = string.Join("|", result.SelectMany(group =>
            group.Windows.Select(window =>
                $"{group.Key}:{group.DisplayName}:{window.Handle.ToInt64()}"
                + $":{(window.IsForeground ? 1 : 0)}:{(window.IsMinimized ? 1 : 0)}")));
        bool changed = !string.Equals(signature, _lastSignature, StringComparison.Ordinal);
        _lastSignature = signature;

        PruneProcessCache();
        return changed;
    }

    private void PruneProcessCache()
    {
        lock (_processCache)
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);
            foreach (uint key in _processCache
                         .Where(pair => pair.Value.CachedUtc < cutoff)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _processCache.Remove(key);
            }
        }
    }

    private string ResolveExecutable(uint pid, out string displayName)
    {
        lock (_processCache)
        {
            if (_processCache.TryGetValue(pid, out ProcessSnapshot? cached)
                && cached is not null
                && (DateTime.UtcNow - cached.CachedUtc).TotalSeconds < 30)
            {
                displayName = cached.DisplayName;
                return cached.ExecutablePath;
            }
        }

        string executable = "";
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            executable = process.MainModule?.FileName ?? "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                       or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // 受保护进程 / 已退出进程 / 32-64 位差异，均回退空路径
        }

        string name = ResolveDisplayName(executable, pid);
        lock (_processCache)
        {
            _processCache[pid] = new ProcessSnapshot(executable, name, DateTime.UtcNow);
        }

        displayName = name;
        return executable;
    }

    private static string ResolveDisplayName(string executable, uint pid)
    {
        if (!string.IsNullOrEmpty(executable))
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(executable);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                {
                    return info.FileDescription;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or IOException)
            {
            }

            string fileName = Path.GetFileNameWithoutExtension(executable);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        try
        {
            using Process process = Process.GetProcessById((int)pid);
            if (!string.IsNullOrWhiteSpace(process.ProcessName))
            {
                return process.ProcessName;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
        }

        return "";
    }
}
