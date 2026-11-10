using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Native;
using static WinDock.Core.Native.NativeMethods;

namespace WinDock.Core.Services;

/// <summary>
/// 原生任务栏隐藏 / 原样恢复（设计决策 D2）。
/// 默认模式 C 不调用 Hide；快照持久化到磁盘，保证主进程被强杀后下次启动仍能恢复。
/// M0 报告把该能力判为「降级」：Build 26200 上未按用户确认做实机写入验证。
/// </summary>
public sealed class TaskbarInterop : ITaskbarInterop
{
    private const string Module = "Taskbar";
    private const string TrayClass = "Shell_TrayWnd";
    private const string SecondaryTrayClass = "Shell_SecondaryTrayWnd";

    private const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>模式 B（精简共存）要关掉的任务栏元素：搜索框 / 任务视图 / 小组件 / 聊天。</summary>
    private static readonly (string Key, string Name)[] CompactValues =
    {
        (SearchKey, "SearchboxTaskbarMode"),
        (AdvancedKey, "ShowTaskViewButton"),
        (AdvancedKey, "TaskbarDa"),
        (AdvancedKey, "TaskbarMn"),
    };

    private readonly ILogService _log;
    private readonly string _snapshotPath;
    private readonly string _compactSnapshotPath;
    private TaskbarSnapshot? _snapshot;
    private int _hideDx;
    private int _hideDy;

    public TaskbarInterop(ILogService log)
    {
        _log = log;
        TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinDock");
        Directory.CreateDirectory(directory);
        _snapshotPath = Path.Combine(directory, "taskbar-snapshot.json");
        _compactSnapshotPath = Path.Combine(directory, "taskbar-compact.json");
    }

    public uint TaskbarCreatedMessage { get; }

    public bool IsHidden { get; private set; }

    public event EventHandler? TaskbarRecreated;

    /// <summary>是否存在上次未恢复的隐藏快照（用于启动时兜底恢复）。</summary>
    public bool HasPendingRestore => File.Exists(_snapshotPath);

    public TaskbarSnapshot Inspect()
    {
        IntPtr handle = FindWindow(TrayClass, null);
        if (handle == IntPtr.Zero)
        {
            _log.Warn(Module, "未找到 Shell_TrayWnd");
            return new TaskbarSnapshot();
        }

        if (!GetWindowRect(handle, out RECT rect))
        {
            _log.Warn(Module, $"GetWindowRect 失败：{Marshal.GetLastWin32Error()}");
            return new TaskbarSnapshot();
        }

        var secondary = new List<IntPtr>();
        var className = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            className.Clear();
            GetClassName(hwnd, className, className.Capacity);
            if (string.Equals(className.ToString(), SecondaryTrayClass, StringComparison.Ordinal))
            {
                secondary.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        RectBounds bounds = ToBounds(rect);
        return new TaskbarSnapshot
        {
            Handle = handle,
            Bounds = bounds,
            Edge = InferEdge(bounds),
            IsAutoHide = false, // M0 判定 AppBar 降级，不注册也不查询
            SecondaryHandles = secondary,
        };
    }

    public bool Hide()
    {
        TaskbarSnapshot snapshot = Inspect();
        if (!snapshot.IsValid)
        {
            _log.Warn(Module, "任务栏快照无效，拒绝隐藏");
            return false;
        }

        (_hideDx, _hideDy) = OffscreenOffset(snapshot);
        if (_hideDx == 0 && _hideDy == 0)
        {
            _log.Warn(Module, "无法推断任务栏边缘，拒绝隐藏");
            return false;
        }

        bool ok = SetWindowPos(snapshot.Handle, IntPtr.Zero,
            snapshot.Bounds.Left + _hideDx, snapshot.Bounds.Top + _hideDy,
            snapshot.Bounds.Width, snapshot.Bounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        if (!ok)
        {
            _log.Error(Module, $"SetWindowPos 隐藏失败：{Marshal.GetLastWin32Error()}");
        }

        foreach (IntPtr handle in snapshot.SecondaryHandles)
        {
            if (GetWindowRect(handle, out RECT r))
            {
                ok &= SetWindowPos(handle, IntPtr.Zero, r.Left + _hideDx, r.Top + _hideDy,
                    r.Right - r.Left, r.Bottom - r.Top,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }

        _snapshot = snapshot;
        IsHidden = ok;
        if (ok)
        {
            SaveSnapshot(snapshot);
            _log.Info(Module, $"任务栏已隐藏，边缘={snapshot.Edge}，原矩形={snapshot.Bounds}");
        }

        return ok;
    }

    public bool Restore()
    {
        TaskbarSnapshot? snapshot = _snapshot ?? LoadSnapshot();
        if (snapshot is null || !snapshot.IsValid)
        {
            IsHidden = false;
            DeleteSnapshot();
            return true;
        }

        bool ok = SetWindowPos(snapshot.Handle, IntPtr.Zero,
            snapshot.Bounds.Left, snapshot.Bounds.Top,
            snapshot.Bounds.Width, snapshot.Bounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        if (!ok)
        {
            _log.Warn(Module, $"SetWindowPos 恢复主任务栏失败：{Marshal.GetLastWin32Error()}");
        }

        foreach (IntPtr handle in snapshot.SecondaryHandles)
        {
            if (GetWindowRect(handle, out RECT r))
            {
                ok &= SetWindowPos(handle, IntPtr.Zero, r.Left - _hideDx, r.Top - _hideDy,
                    r.Right - r.Left, r.Bottom - r.Top,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }

        IsHidden = false;
        _snapshot = null;
        DeleteSnapshot();
        _log.Info(Module, ok
            ? Loc.Instance.T("Log_TaskbarRestored")
            : "任务栏恢复未完全成功，请手动检查");
        return ok;
    }

    public void NotifyTaskbarCreated()
    {
        _log.Info(Module, "收到 TaskbarCreated（Explorer 重启）");
        TaskbarRecreated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 6.13 模式 B「精简共存」：原生任务栏只保留托盘区——把搜索框、任务视图、小组件、聊天
    /// 图标通过 HKCU 用户设置关掉（只动当前用户，不改系统项，需求 4.4）。
    /// 关闭时按快照原样写回，**首次开启前的旧值会被持久化**，进程崩溃也能在下次启动还原。
    /// </summary>
    public bool ApplyCompactMode(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (File.Exists(_compactSnapshotPath))
                {
                    return true; // 已处于精简态，不覆盖快照（否则会把 0 当成"旧值"存起来）
                }

                var previous = new Dictionary<string, int>();
                foreach ((string key, string name) in CompactValues)
                {
                    int? old = ReadDword(key, name);
                    if (old.HasValue)
                    {
                        previous[$"{key}|{name}"] = old.Value;
                    }
                }

                File.WriteAllText(_compactSnapshotPath, JsonSerializer.Serialize(previous));
                foreach ((string key, string name) in CompactValues)
                {
                    int error = WriteDword(key, name, 0);
                    if (error != 0)
                    {
                        _log.Warn(Module, $"精简共存：写入 {name} 失败 Win32Error={error}");
                    }
                }

                BroadcastTraySettings();
                _log.Info(Module, "任务栏模式=精简共存：已隐藏搜索/任务视图/小组件/聊天");
                return true;
            }

            if (!File.Exists(_compactSnapshotPath))
            {
                return true;
            }

            Dictionary<string, int>? snapshot = null;
            try
            {
                snapshot = JsonSerializer.Deserialize<Dictionary<string, int>>(
                    File.ReadAllText(_compactSnapshotPath));
            }
            catch (JsonException ex)
            {
                _log.Error(Module, "精简共存快照损坏，按默认值还原", ex);
            }

            foreach ((string key, string name) in CompactValues)
            {
                int restore = snapshot is not null && snapshot.TryGetValue($"{key}|{name}", out int value)
                    ? value
                    : -1; // 快照里没有记录 = 原来不存在该值
                if (restore >= 0)
                {
                    WriteDword(key, name, restore);
                }
            }

            File.Delete(_compactSnapshotPath);
            BroadcastTraySettings();
            _log.Info(Module, "已还原原生任务栏的搜索/任务视图/小组件设置");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(Module, "应用精简共存失败", ex);
            return false;
        }
    }

    public void Dispose()
    {
        // 不自动 Restore：组合根按配置与退出语义决定，避免服务对象被提前释放时误恢复。
    }

    private static (int Dx, int Dy) OffscreenOffset(TaskbarSnapshot snapshot)
    {
        const int margin = 8;
        return snapshot.Edge switch
        {
            TaskbarEdge.Top => (0, -snapshot.Bounds.Height - margin),
            TaskbarEdge.Bottom => (0, snapshot.Bounds.Height + margin),
            TaskbarEdge.Left => (-snapshot.Bounds.Width - margin, 0),
            TaskbarEdge.Right => (snapshot.Bounds.Width + margin, 0),
            _ => (0, 0),
        };
    }

    /// <summary>按实测矩形动态推断边缘，禁止硬编码「底部」（M0：本机任务栏在顶部）。</summary>
    private static TaskbarEdge InferEdge(RectBounds bounds)
    {
        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);
        if (bounds.Width >= bounds.Height)
        {
            if (bounds.Top <= 0)
            {
                return TaskbarEdge.Top;
            }

            return bounds.Bottom >= screenHeight - 2 ? TaskbarEdge.Bottom
                : bounds.Top < screenHeight / 2 ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        if (bounds.Left <= 0)
        {
            return TaskbarEdge.Left;
        }

        return bounds.Right >= screenWidth - 2 ? TaskbarEdge.Right
            : bounds.Left < screenWidth / 2 ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    private void SaveSnapshot(TaskbarSnapshot snapshot)
    {
        var state = new SnapshotState
        {
            Handle = snapshot.Handle.ToInt64(),
            Left = snapshot.Bounds.Left,
            Top = snapshot.Bounds.Top,
            Right = snapshot.Bounds.Right,
            Bottom = snapshot.Bounds.Bottom,
            Edge = snapshot.Edge.ToString(),
            OffsetX = _hideDx,
            OffsetY = _hideDy,
            Secondary = new long[snapshot.SecondaryHandles.Count],
        };
        for (int i = 0; i < snapshot.SecondaryHandles.Count; i++)
        {
            state.Secondary[i] = snapshot.SecondaryHandles[i].ToInt64();
        }

        try
        {
            File.WriteAllText(_snapshotPath, JsonSerializer.Serialize(state));
        }
        catch (IOException ex)
        {
            _log.Error(Module, "任务栏快照持久化失败", ex);
        }
    }

    private TaskbarSnapshot? LoadSnapshot()
    {
        try
        {
            if (!File.Exists(_snapshotPath))
            {
                return null;
            }

            SnapshotState? state = JsonSerializer.Deserialize<SnapshotState>(
                File.ReadAllText(_snapshotPath));
            if (state is null || state.Handle == 0)
            {
                return null;
            }

            _hideDx = state.OffsetX;
            _hideDy = state.OffsetY;
            Enum.TryParse(state.Edge, out TaskbarEdge edge);
            return new TaskbarSnapshot
            {
                Handle = new IntPtr(state.Handle),
                Bounds = new RectBounds(state.Left, state.Top, state.Right, state.Bottom),
                Edge = edge,
                SecondaryHandles = Array.ConvertAll(state.Secondary ?? Array.Empty<long>(),
                    value => new IntPtr(value)),
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Error(Module, "读取任务栏快照失败", ex);
            return null;
        }
    }

    private void DeleteSnapshot()
    {
        try
        {
            if (File.Exists(_snapshotPath))
            {
                File.Delete(_snapshotPath);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class SnapshotState
    {
        public long Handle { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public string Edge { get; set; } = "Unknown";
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public long[] Secondary { get; set; } = Array.Empty<long>();
    }
}
