namespace WinDock.Core.Services;

/// <summary>帧率统计快照（WD-038 / AC-21）。</summary>
/// <param name="Frames">统计窗口内的帧数。</param>
/// <param name="Last1s">最近 1 秒的 FPS（帧计数）。</param>
/// <param name="WindowAvg">窗口内平均 FPS（1000 / 平均帧间隔，含空闲间隔，仅供参考）。</param>
/// <param name="WindowP5">窗口内逐帧瞬时 FPS 的 P5（含空闲间隔）。</param>
/// <param name="WindowMin">窗口内最小瞬时 FPS。</param>
/// <param name="Dropped">活跃突发内累计丢帧数（间隔 &gt; 1.5×标称帧间隔且 ≤ 空闲阈值）。</param>
/// <param name="Bursts">活跃突发数（相邻帧间隔 ≤ 空闲阈值的一段连续帧）。</param>
/// <param name="ActiveAvg">活跃突发内的平均 FPS（AC-21 的判定口径）。</param>
/// <param name="ActiveP5">活跃突发内瞬时 FPS 的 P5。</param>
/// <param name="ActiveMin">活跃突发内最小瞬时 FPS。</param>
public sealed record FrameRateSnapshot(
    int Frames, double Last1s, double WindowAvg, double WindowP5, double WindowMin,
    long Dropped, int Bursts, double ActiveAvg, double ActiveP5, double ActiveMin);

/// <summary>
/// WD-038 / AC-21 动画帧率统计（纯逻辑、零 UI 依赖，离线可自检）。
///
/// 数据源是 CompositionTarget.Rendering 的每帧回调（AnimationEngine 只在
/// 动画活跃时订阅、收敛即退订，M1 D4）——因此 FPS 只在动画/悬停期间有意义，
/// 空闲态没有帧、也不产出指标，这与 AC-21「连续悬停 60s」的口径一致。
/// 本类自身不持任何 Timer，只在 AddFrame 时顺带做窗口裁剪。
///
/// 口径说明：时间平均 FPS（Window*）会被两段动画之间的空闲期稀释，不能判
/// 「动画是否流畅」；判定用 Active*——把间隔 ≤ IdleGapMs 的连续帧视为一个
/// 活跃突发，只统计突发内部逐帧间隔（实测第一版探针：悬停轮换 60s 只有
/// ~293 帧、时间平均 4.9 FPS，但动画突发内应是 60FPS）。
/// </summary>
public sealed class FrameRateCounter
{
    private const double NominalFrameMs = 1000.0 / 60.0;         // 16.67ms @60Hz
    private const double DropThresholdMs = NominalFrameMs * 1.5; // 25ms
    private const double IdleGapMs = 100.0;  // >100ms 的间隔视为两段动画之间的空闲

    private readonly Queue<long> _ticks = new();
    private long _lastTick = -1;
    private long _dropped;

    /// <summary>记一帧。tickMs 建议用 Environment.TickCount64（单调、无时区）。</summary>
    public void AddFrame(long tickMs)
    {
        if (_lastTick >= 0 && tickMs > _lastTick)
        {
            double gap = tickMs - _lastTick;
            // 只有「活跃突发内」的超长间隔算丢帧；空闲分隔（动画收敛退订后
            // 到下一次动画开始）不算丢帧。
            if (gap > DropThresholdMs && gap <= IdleGapMs)
            {
                _dropped++;
            }
        }

        _lastTick = tickMs;
        _ticks.Enqueue(tickMs);
        while (_ticks.Count > 0 && tickMs - _ticks.Peek() > WindowMs)
        {
            _ticks.Dequeue();
        }
    }

    /// <summary>取当前 60s 窗口的统计（只读，不改内部状态语义）。</summary>
    public FrameRateSnapshot Snapshot()
    {
        long[] ticks = _ticks.ToArray();
        if (ticks.Length == 0)
        {
            return new FrameRateSnapshot(0, 0, 0, 0, 0, _dropped, 0, 0, 0, 0);
        }

        long now = ticks[^1];
        int last1s = 0;
        for (int i = ticks.Length - 1; i >= 0; i--)
        {
            if (now - ticks[i] <= 1000)
            {
                last1s++;
            }
            else
            {
                break;
            }
        }

        // 逐帧间隔 → 瞬时 FPS；窗口均值 = 1000 / 平均间隔。
        var instant = new List<double>(Math.Max(0, ticks.Length - 1));
        double total = 0;
        var active = new List<double>();
        double activeGapTotal = 0;
        int bursts = 0;
        bool inBurst = false;
        for (int i = 1; i < ticks.Length; i++)
        {
            double gap = ticks[i] - ticks[i - 1];
            if (gap <= 0)
            {
                continue;
            }

            total += gap;
            double fps = 1000.0 / gap;
            instant.Add(fps);
            if (gap <= IdleGapMs)
            {
                active.Add(fps);
                activeGapTotal += gap;
                inBurst = true;
            }
            else
            {
                if (inBurst)
                {
                    bursts++;
                    inBurst = false;
                }
            }
        }

        if (inBurst)
        {
            bursts++;
        }

        double windowAvg = instant.Count == 0 ? 0 : 1000.0 * instant.Count / total;
        instant.Sort();
        double windowP5 = instant.Count == 0 ? 0
            : instant[Math.Min(instant.Count - 1, (int)Math.Floor(instant.Count * 0.05))];
        double windowMin = instant.Count == 0 ? 0 : instant[0];

        active.Sort();
        double activeAvg = active.Count == 0 ? 0
            : Math.Round(1000.0 * active.Count / activeGapTotal, 2);
        double activeP5 = active.Count == 0 ? 0
            : active[Math.Min(active.Count - 1, (int)Math.Floor(active.Count * 0.05))];
        double activeMin = active.Count == 0 ? 0 : active[0];

        return new FrameRateSnapshot(ticks.Length, last1s,
            Math.Round(windowAvg, 2), Math.Round(windowP5, 2), Math.Round(windowMin, 2),
            _dropped, bursts, activeAvg, Math.Round(activeP5, 2), Math.Round(activeMin, 2));
    }

    private const int WindowMs = 60_000;

    public void Reset()
    {
        _ticks.Clear();
        _lastTick = -1;
        _dropped = 0;
    }
}
