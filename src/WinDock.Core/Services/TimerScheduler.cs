using WinDock.Core.Interfaces;

namespace WinDock.Core.Services;

/// <summary>
/// 单一延时调度器（M2 D3 / 需求 14.3）。内部只有一个 System.Threading.Timer，
/// 且采用 **按需单次触发**：无待办时 Change(Timeout.Infinite)，空闲零回调，
/// 避免 M1 曾出现的「周期性空转导致 21% 空闲 CPU」。
/// 同 key 再次 Schedule 会覆盖前一次，天然实现「鼠标移开后又移回则取消隐藏」的去抖。
/// 回调统一派发到构造时捕获的 UI SynchronizationContext。
/// </summary>
public sealed class TimerScheduler : ITimerScheduler
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (DateTime DueUtc, Action Action)> _pending =
        new(StringComparer.Ordinal);
    private readonly SynchronizationContext? _context;
    private readonly Timer _timer;
    private bool _disposed;

    public TimerScheduler(SynchronizationContext? context = null)
    {
        _context = context ?? SynchronizationContext.Current;
        _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Schedule(string key, int delayMs, Action action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending[key] = (DateTime.UtcNow.AddMilliseconds(Math.Max(0, delayMs)), action);
            Rearm();
        }
    }

    public void Cancel(string key)
    {
        lock (_sync)
        {
            if (_pending.Remove(key))
            {
                Rearm();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending.Clear();
        }

        _timer.Dispose();
    }

    /// <summary>在锁内调用：把 Timer 重排到最近的一个到期时刻；无待办则完全停用。</summary>
    private void Rearm()
    {
        if (_disposed || _pending.Count == 0)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        DateTime soonest = _pending.Values.Min(v => v.DueUtc);
        double delay = (soonest - DateTime.UtcNow).TotalMilliseconds;
        long due = Math.Max(0, (long)Math.Ceiling(delay));
        _timer.Change(due, Timeout.Infinite);
    }

    private void OnTick(object? state)
    {
        List<Action>? due = null;
        lock (_sync)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            foreach (string key in _pending.Keys.ToList())
            {
                var entry = _pending[key];
                if (entry.DueUtc > now)
                {
                    continue;
                }

                _pending.Remove(key);
                (due ??= new List<Action>()).Add(entry.Action);
            }

            Rearm();
        }

        if (due is null)
        {
            return;
        }

        foreach (Action action in due)
        {
            if (_context is not null)
            {
                _context.Post(_ => Run(action), null);
            }
            else
            {
                Run(action);
            }
        }
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // 单个回调失败不应阻断后续到期任务；业务异常由调用方自行记录日志。
        }
    }
}
