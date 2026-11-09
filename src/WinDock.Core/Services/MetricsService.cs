using System.Diagnostics;
using WinDock.Core.Interfaces;

namespace WinDock.Core.Services;

/// <summary>
/// 本地打点与 CPU 突发归因（WD-033 / K-11 / K-13 / 8.6 不上报）。
///
/// 设计要点：
/// - 计数/耗时都是内存字典 + 一把锁，单次调用在纳秒级，不引入 IO；
/// - 1Hz CPU 采样跑在**独立后台线程**（专用 System.Threading.Timer），绝不用
///   ITimerScheduler —— 归因工具本身不能唤醒 UI 线程，否则会污染被测对象；
/// - 采样窗口内进程 CPU 超过阈值（默认 15ms/s ≈ 1.5%）才写一条 Info 归因行，
///   内容为该窗口内各指标计数增量；每 30s 输出一次全量汇总。
/// - Mark(checkpoint) 落 Info 日志，用于冷启动 / 首帧等里程碑时延换算。
/// </summary>
public sealed class MetricsService : IMetricsService
{
    private const string Module = "Metrics";
    private const int RingSize = 64; // Mark 时间戳环，自检断言用

    private readonly ILogService _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, MetricsStat> _stats = new(StringComparer.Ordinal);
    private readonly Queue<(DateTime At, string Name)> _marks = new();
    private readonly Queue<double> _recentLatencies = new();

    private Timer? _sampler;
    private Process? _process;
    private TimeSpan _lastCpu;
    private DateTime _lastSample = DateTime.UtcNow;
    private DateTime _lastSummary = DateTime.UtcNow;
    private Dictionary<string, long> _lastWindow = new(StringComparer.Ordinal);
    private int _spikeThresholdMs = 15;
    private int _summaryIntervalS = 30;
    private bool _disposed;

    public MetricsService(ILogService log)
    {
        _log = log;
        try
        {
            _process = Process.GetCurrentProcess(); // 复用句柄，避免每窗口分配
            _lastCpu = _process.TotalProcessorTime;
        }
        catch
        {
            _lastCpu = TimeSpan.Zero;
        }
    }

    public void Mark(string checkpoint)
    {
        lock (_sync)
        {
            _marks.Enqueue((DateTime.UtcNow, checkpoint));
            while (_marks.Count > RingSize)
            {
                _marks.Dequeue();
            }
        }

        _log.Info(Module, $"mark {checkpoint}");
    }

    public void Count(string metric, long delta = 1)
    {
        lock (_sync)
        {
            _stats[metric] = _stats.TryGetValue(metric, out MetricsStat s)
                ? new MetricsStat(s.Count + delta, s.TotalMs, s.MaxMs)
                : new MetricsStat(delta, 0, 0);
        }
    }

    public void Record(string metric, double milliseconds)
    {
        lock (_sync)
        {
            _stats[metric] = _stats.TryGetValue(metric, out MetricsStat s)
                ? new MetricsStat(s.Count + 1, s.TotalMs + milliseconds, Math.Max(s.MaxMs, milliseconds))
                : new MetricsStat(1, milliseconds, milliseconds);

            _recentLatencies.Enqueue(milliseconds);
            while (_recentLatencies.Count > RingSize)
            {
                _recentLatencies.Dequeue();
            }
        }
    }

    public IReadOnlyDictionary<string, MetricsStat> Snapshot()
    {
        lock (_sync)
        {
            return new Dictionary<string, MetricsStat>(_stats, StringComparer.Ordinal);
        }
    }

    /// <summary>启动进程 CPU 采样与突发归因（默认关闭，回归测试时开启）。</summary>
    public void StartCpuSampler(int spikeThresholdMs = 15, int summaryIntervalS = 30)
    {
        lock (_sync)
        {
            if (_disposed || _sampler is not null)
            {
                return;
            }

            _spikeThresholdMs = Math.Max(1, spikeThresholdMs);
            _summaryIntervalS = Math.Max(5, summaryIntervalS);
            // 2s 节拍：每次后台唤醒都可能被进程 CPU 记账计一个刻度，
            // 1Hz 采样器自身会污染 8.1 指标（WD-033 实测复盘）。
            _sampler = new Timer(OnSample, null, 2000, 2000);
            _log.Info(Module, $"CPU 采样已启动：突发阈值={_spikeThresholdMs}ms/2s，汇总间隔={_summaryIntervalS}s");
        }
    }

    private void OnSample(object? state)
    {
        if (_disposed)
        {
            return;
        }

        double cpuDeltaMs;
        double windowS;
        try
        {
            TimeSpan cpu = _process!.TotalProcessorTime;
            cpuDeltaMs = (cpu - _lastCpu).TotalMilliseconds;
            windowS = Math.Max(0.25, (DateTime.UtcNow - _lastSample).TotalSeconds);
            _lastCpu = cpu;
        }
        catch
        {
            return; // 进程句柄异常时静默放弃本窗口
        }

        DateTime now = DateTime.UtcNow;
        _lastSample = now;

        Dictionary<string, long> current;
        Dictionary<string, MetricsStat> stats;
        lock (_sync)
        {
            stats = new Dictionary<string, MetricsStat>(_stats, StringComparer.Ordinal);
            current = stats.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
        }

        Dictionary<string, long> delta = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, long> kv in current)
        {
            long prev = _lastWindow.GetValueOrDefault(kv.Key);
            if (kv.Value > prev)
            {
                delta[kv.Key] = kv.Value - prev;
            }
        }

        _lastWindow = current;

        if (cpuDeltaMs / windowS >= _spikeThresholdMs && delta.Count > 0)
        {
            _log.Info(Module,
                $"cpu-spike window: {cpuDeltaMs:F1}ms/{windowS:F0}s → " +
                string.Join(", ", delta.OrderBy(kv => -kv.Value).Select(kv => $"{kv.Key}×{kv.Value}")));
        }

        if ((now - _lastSummary).TotalSeconds >= _summaryIntervalS)
        {
            _lastSummary = now;
            string summary = stats.Count == 0
                ? "(no metrics)"
                : string.Join(", ", stats.OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key}: n={kv.Value.Count}, total={kv.Value.TotalMs:F0}ms, max={kv.Value.MaxMs:F0}ms"));
            _log.Info(Module, $"summary cpu={cpuDeltaMs / windowS:F1}ms/s | {summary}");
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
        }

        _sampler?.Dispose();
    }
}
