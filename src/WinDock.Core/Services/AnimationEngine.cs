using System.Windows.Media;
using WinDock.Core.Interfaces;

namespace WinDock.Core.Services;

/// <summary>
/// 单一动画时间轴（M1 D4 / M2 D3 / 需求 14.3）：只在有动画需求时订阅
/// CompositionTarget.Rendering，全部通道收敛后立即取消订阅，保证空闲时不跑渲染循环。
///
/// 通道（同一帧内统一计算、统一输出）：
///   1. 邻域余弦衰减放大 + 水平位移补偿（FR-201）
///   2. 点击缩放反馈（FR-206）
///   3. 启动 / 注意跳动（FR-202 / FR-203）
///   4. 运行指示点淡入淡出（FR-204）
///   5. 整条 Dock 滑入滑出（FR-601）
///
/// 底边对齐由 UI 的 RenderTransformOrigin=(0.5,1) 保证；控件只接收帧数据，不自持动画。
/// </summary>
public sealed class AnimationEngine : IAnimationEngine
{
    private const double Epsilon = 0.0008;

    private double[] _current = Array.Empty<double>();
    private double[] _target = Array.Empty<double>();
    private double[] _offsets = Array.Empty<double>();
    private double[] _bounceOffsets = Array.Empty<double>();
    private double[] _indicatorCurrent = Array.Empty<double>();
    private double[] _indicatorTarget = Array.Empty<double>();

    private int? _hoverIndex;
    private int _itemCount;
    private double _baseSize = 48;
    private double _spacing = 10;
    private double _magnifyScale = 1.75;
    private double _radius = 2.5;
    private double _durationMs = 120;
    private double _speed = 1.0;
    private bool _reducedMotion;
    private string _decay = "cosine";

    private int _tapIndex = -1;
    private DateTime _tapStartUtc;
    private DateTime _lastFrameUtc;
    private bool _subscribed;

    // M2 跳动通道
    private int _bounceIndex = -1;
    private DateTime _bounceStartUtc;
    private double _bounceHeight;
    private double _bounceDurationMs = 600;

    // M2 指示点与滑动通道
    private double _indicatorFadeMs = 150;
    private double _slideDurationMs = 180;
    private double _slideCurrent;
    private double _slideTarget;

    public event EventHandler<AnimationFrameEventArgs>? Frame;

    /// <inheritdoc />
    public bool IsRunning => _subscribed;

    /// <inheritdoc />
    public double SlideOffset => _slideCurrent;

    public void Configure(double magnifyScale, double radius, int durationMs,
        double speedMultiplier, bool reducedMotion, string decay = "cosine")
    {
        _magnifyScale = magnifyScale;
        _radius = Math.Max(0.5, radius);
        _durationMs = durationMs;
        _speed = Math.Max(0.1, speedMultiplier);
        _reducedMotion = reducedMotion;
        _decay = decay;
        if (reducedMotion)
        {
            // 系统「减少动态效果」：立即落位，不留任何进行中的通道。
            _tapIndex = -1;
            _bounceIndex = -1;
        }

        ComputeTargets();
        EnsureRunning();
    }

    public void ConfigureMotion(int indicatorFadeMs, int slideDurationMs)
    {
        _indicatorFadeMs = Math.Clamp(indicatorFadeMs, 0, 600);
        _slideDurationMs = Math.Clamp(slideDurationMs, 30, 800);
    }

    public void SetHover(int? hoverIndex, int itemCount, double baseSize, double spacing)
    {
        int count = Math.Max(0, itemCount);
        double size = Math.Max(1, baseSize);
        double gap = Math.Max(0, spacing);
        if (_hoverIndex == hoverIndex && _itemCount == count
            && Math.Abs(_baseSize - size) < 0.001
            && Math.Abs(_spacing - gap) < 0.001)
        {
            // 图标放大改变命中边界时 MouseEnter 会重复触发；同一状态不重算目标，
            // 否则会无意义地唤醒渲染循环（M2 悬停静止曾实测 22% CPU）。
            return;
        }

        _hoverIndex = hoverIndex;
        _baseSize = size;
        _spacing = gap;
        ResizeArrays(count);
        ComputeTargets();
        EnsureRunning();
    }

    public void Tap(int index)
    {
        if (_reducedMotion)
        {
            return;
        }

        _tapIndex = index;
        _tapStartUtc = DateTime.UtcNow;
        ComputeTargets();
        EnsureRunning();
    }

    public void Bounce(int index, double iconHeight, int count)
    {
        if (_reducedMotion || index < 0 || index >= _itemCount)
        {
            return;
        }

        int hops = Math.Clamp(count, 1, 3);
        _bounceIndex = index;
        _bounceStartUtc = DateTime.UtcNow;
        _bounceHeight = Math.Max(1, iconHeight) * 0.55; // FR-202：高度为图标 55%
        _bounceDurationMs = 300.0 * hops;               // 2 次 = 600ms ±50ms
        _bounceCount = hops;
        _bounceDown = false;
        EnsureRunning();
    }

    private int _bounceCount = 2;
    private bool _bounceDown; // WD-032：true 时跳动方向向下（最小化落入 Dock）

    public void Attention(int index, double iconHeight)
    {
        if (_reducedMotion || index < 0 || index >= _itemCount)
        {
            return;
        }

        // FR-203：由 ShellHook 的 HSHELL_FLASH（MainWindow 注册 RegisterShellHookWindow）
        // 触发；一次 200ms 小幅跳动（原始高度 20%）。
        _bounceIndex = index;
        _bounceStartUtc = DateTime.UtcNow;
        _bounceHeight = Math.Max(1, iconHeight) * 0.2;
        _bounceDurationMs = 200;
        _bounceCount = 1;
        _bounceDown = false;
        EnsureRunning();
    }

    public void Drop(int index, double iconHeight)
    {
        if (_reducedMotion || index < 0 || index >= _itemCount)
        {
            return;
        }

        // WD-032 / 6.9：最小化时图标向下「落入」Dock，300ms ease-in，一次。
        _bounceIndex = index;
        _bounceStartUtc = DateTime.UtcNow;
        _bounceHeight = Math.Max(1, iconHeight) * 0.5;
        _bounceDurationMs = 300;
        _bounceCount = 1;
        _bounceDown = true;
        EnsureRunning();
    }

    public void StopBounce()
    {
        if (_bounceIndex < 0)
        {
            return;
        }

        _bounceIndex = -1;
        Array.Clear(_bounceOffsets);
        EmitFrame(IsSettled());
    }

    public void SetIndicatorStates(bool[] running, bool[] foreground)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(foreground);
        int count = Math.Min(running.Length, foreground.Length);
        bool structureChanged = count != _itemCount;
        ResizeArrays(count);
        for (int i = 0; i < count; i++)
        {
            _indicatorTarget[i] = running[i] ? 1.0 : 0.0;
            if (structureChanged)
            {
                // 冷启动 / 结构重建：指示点直接就位，避免整排图标集体淡入。
                _indicatorCurrent[i] = _indicatorTarget[i];
            }
        }

        // 即使没有正在运行的渲染循环，也要把「立即就位」的帧推给 UI。
        EmitFrame(IsSettled());
        EnsureRunning();
    }

    public void Slide(double target)
    {
        _slideTarget = Math.Max(0, target);
        if (_reducedMotion)
        {
            _slideCurrent = _slideTarget;
            EmitFrame(IsSettled());
            return;
        }

        EnsureRunning();
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }
    }

    private void ResizeArrays(int count)
    {
        _itemCount = count;
        if (_current.Length == count)
        {
            return;
        }

        var current = new double[count];
        var target = new double[count];
        var offsets = new double[count];
        var bounce = new double[count];
        var indicatorCurrent = new double[count];
        var indicatorTarget = new double[count];
        int keep = Math.Min(_current.Length, count);
        for (int i = 0; i < count; i++)
        {
            current[i] = i < keep ? _current[i] : 1.0;
            target[i] = 1.0;
            indicatorCurrent[i] = i < keep ? _indicatorCurrent[i] : 0.0;
            indicatorTarget[i] = i < keep ? _indicatorTarget[i] : 0.0;
        }

        _current = current;
        _target = target;
        _offsets = offsets;
        _bounceOffsets = bounce;
        _indicatorCurrent = indicatorCurrent;
        _indicatorTarget = indicatorTarget;
        if (_bounceIndex >= count)
        {
            _bounceIndex = -1;
        }
    }

    private void ComputeTargets()
    {
        bool tapping = _tapIndex >= 0
                       && (DateTime.UtcNow - _tapStartUtc).TotalMilliseconds < 60;
        for (int i = 0; i < _target.Length; i++)
        {
            double scale = 1.0;
            if (_hoverIndex.HasValue && !_reducedMotion)
            {
                double distance = Math.Abs(i - _hoverIndex.Value);
                double weight = _decay switch
                {
                    "gaussian" => Math.Exp(-4.0 * distance * distance / (_radius * _radius)),
                    "linear" => Math.Max(0.0, 1.0 - distance / _radius),
                    _ => 0.5 * (1.0 + Math.Cos(Math.PI * Math.Min(distance / _radius, 1.0))),
                };
                scale = 1.0 + (_magnifyScale - 1.0) * weight;
            }

            if (tapping && i == _tapIndex)
            {
                scale *= 0.92; // FR-206 点击反馈
            }

            _target[i] = scale;
        }
    }

    private void EnsureRunning()
    {
        if (_subscribed || IsSettled())
        {
            // 目标与当前已一致时完全不订阅渲染循环。窗口事件常会调用
            // SetHover(null)，若无条件订阅就会白跑一帧（M1 空闲 CPU 21% 的根因）。
            return;
        }

        _lastFrameUtc = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRendering;
        _subscribed = true;
    }

    private bool IsSettled()
    {
        if (_current.Length != _target.Length || _bounceIndex >= 0 || _tapIndex >= 0)
        {
            return false;
        }

        for (int i = 0; i < _current.Length; i++)
        {
            if (Math.Abs(_current[i] - _target[i]) > Epsilon
                || Math.Abs(_indicatorCurrent[i] - _indicatorTarget[i]) > Epsilon
                || Math.Abs(_bounceOffsets[i]) > Epsilon)
            {
                return false;
            }
        }

        return Math.Abs(_slideCurrent - _slideTarget) <= 0.5;
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = Math.Min((now - _lastFrameUtc).TotalMilliseconds, 64);
        _lastFrameUtc = now;

        if (_tapIndex >= 0 && (now - _tapStartUtc).TotalMilliseconds >= 60)
        {
            _tapIndex = -1;
            ComputeTargets();
        }

        double timeConstant = Math.Max(8.0, _durationMs / Math.Max(0.5, _speed)) / 3.0;
        double blend = 1.0 - Math.Exp(-elapsed / timeConstant);
        for (int i = 0; i < _current.Length; i++)
        {
            double next = _current[i] + (_target[i] - _current[i]) * blend;
            _current[i] = Math.Abs(next - _target[i]) < Epsilon ? _target[i] : next;
            AdvanceIndicator(i, elapsed, blend);
        }

        AdvanceSlide(elapsed);
        bool bouncing = AdvanceBounce(now);
        bool settled = IsSettled();

        EmitFrame(settled);

        if (settled && !bouncing && _tapIndex < 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }
    }

    private void AdvanceIndicator(int index, double elapsed, double blend)
    {
        double target = _indicatorTarget[index];
        double current = _indicatorCurrent[index];
        if (Math.Abs(current - target) <= Epsilon)
        {
            _indicatorCurrent[index] = target;
            return;
        }

        if (_indicatorFadeMs <= 0)
        {
            _indicatorCurrent[index] = target;
            return;
        }

        // 固定时长淡入 / 淡出：按每帧时间占比线性推进，避免指数拖尾超过 150ms。
        double step = elapsed / _indicatorFadeMs;
        _indicatorCurrent[index] = current < target
            ? Math.Min(target, current + step)
            : Math.Max(target, current - step);
        _ = blend;
    }

    private void AdvanceSlide(double elapsed)
    {
        double diff = _slideTarget - _slideCurrent;
        if (Math.Abs(diff) <= 0.5)
        {
            _slideCurrent = _slideTarget;
            return;
        }

        // 180ms 指数缓动（滑入 ease-out / 滑出 ease-in 的统一近似，D3 单一时间轴）。
        double blend = 1.0 - Math.Exp(-elapsed / Math.Max(8.0, _slideDurationMs / 3.0));
        _slideCurrent += diff * blend;
        if (Math.Abs(_slideTarget - _slideCurrent) <= 0.5)
        {
            _slideCurrent = _slideTarget;
        }
    }

    /// <returns>是否仍在跳动（解析式驱动，不引入额外 Timer）。</returns>
    private bool AdvanceBounce(DateTime now)
    {
        Array.Clear(_bounceOffsets);
        if (_bounceIndex < 0)
        {
            return false;
        }

        double elapsed = (now - _bounceStartUtc).TotalMilliseconds;
        if (elapsed >= _bounceDurationMs)
        {
            _bounceIndex = -1;
            return false;
        }

        double period = _bounceDurationMs / _bounceCount;
        int hop = (int)(elapsed / period);
        double local = (elapsed - (hop * period)) / period;
        double damping = Math.Pow(0.35, hop); // FR-202：第 1 次 100%、第 2 次 35%
        double direction = _bounceDown ? 1.0 : -1.0; // 负值向上、正值向下（WD-032）
        _bounceOffsets[_bounceIndex] =
            direction * _bounceHeight * damping * Math.Sin(Math.PI * local);
        return true;
    }

    private void EmitFrame(bool settled)
    {
        ComputeOffsets();
        Frame?.Invoke(this, new AnimationFrameEventArgs(
            (double[])_current.Clone(),
            (double[])_offsets.Clone(),
            settled,
            (double[])_bounceOffsets.Clone(),
            (double[])_indicatorCurrent.Clone(),
            _slideCurrent));
    }

    /// <summary>按放大后的实际宽度重排中心点，结果即每项相对原位的水平位移。</summary>
    private void ComputeOffsets()
    {
        double cursor = 0;
        for (int i = 0; i < _current.Length; i++)
        {
            double width = _baseSize * _current[i];
            double center = cursor + (width / 2.0);
            double originalCenter = (i * (_baseSize + _spacing)) + (_baseSize / 2.0);
            _offsets[i] = center - originalCenter;
            cursor += width + _spacing;
        }
    }
}
