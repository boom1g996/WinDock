using System.Windows.Media;
using WinDock.Core.Interfaces;

namespace WinDock.Core.Services;

/// <summary>
/// 单一动画时间轴（设计决策 D4 / 需求 14.3）：只在有动画需求时订阅
/// CompositionTarget.Rendering，收敛后立即取消订阅，保证空闲时不跑渲染循环。
/// 邻域余弦衰减放大 + 水平位移补偿，避免图标重叠；底边对齐由 UI 的
/// RenderTransformOrigin=(0.5,1) 保证。
/// </summary>
public sealed class AnimationEngine : IAnimationEngine
{
    private const double Epsilon = 0.0008;

    private double[] _current = Array.Empty<double>();
    private double[] _target = Array.Empty<double>();
    private double[] _offsets = Array.Empty<double>();

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

    public event EventHandler<AnimationFrameEventArgs>? Frame;

    public void Configure(double magnifyScale, double radius, int durationMs,
        double speedMultiplier, bool reducedMotion, string decay = "cosine")
    {
        _magnifyScale = magnifyScale;
        _radius = Math.Max(0.5, radius);
        _durationMs = durationMs;
        _speed = Math.Max(0.1, speedMultiplier);
        _reducedMotion = reducedMotion;
        _decay = decay;
        ComputeTargets();
        EnsureRunning();
    }

    public void SetHover(int? hoverIndex, int itemCount, double baseSize, double spacing)
    {
        _hoverIndex = hoverIndex;
        _itemCount = Math.Max(0, itemCount);
        _baseSize = Math.Max(1, baseSize);
        _spacing = Math.Max(0, spacing);
        ResizeArrays();
        ComputeTargets();
        EnsureRunning();
    }

    public void Tap(int index)
    {
        _tapIndex = index;
        _tapStartUtc = DateTime.UtcNow;
        ComputeTargets();
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

    private void ResizeArrays()
    {
        if (_current.Length == _itemCount)
        {
            return;
        }

        var current = new double[_itemCount];
        var target = new double[_itemCount];
        var offsets = new double[_itemCount];
        int keep = Math.Min(_current.Length, _itemCount);
        for (int i = 0; i < _itemCount; i++)
        {
            current[i] = i < keep ? _current[i] : 1.0;
            target[i] = 1.0;
        }

        _current = current;
        _target = target;
        _offsets = offsets;
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
        if (_subscribed)
        {
            return;
        }

        // 目标与当前已经一致时完全不订阅渲染循环。窗口事件常会调用
        // SetHover(null)，若无条件订阅就会白跑一帧 WPF 渲染（空闲 CPU 主要来源之一）。
        if (IsSettled())
        {
            return;
        }

        _lastFrameUtc = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRendering;
        _subscribed = true;
    }

    private bool IsSettled()
    {
        if (_current.Length != _target.Length)
        {
            return false;
        }

        for (int i = 0; i < _current.Length; i++)
        {
            if (Math.Abs(_current[i] - _target[i]) > Epsilon)
            {
                return false;
            }
        }

        return true;
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
        bool settled = true;

        for (int i = 0; i < _current.Length; i++)
        {
            double next = _current[i] + (_target[i] - _current[i]) * blend;
            if (Math.Abs(next - _target[i]) < Epsilon)
            {
                next = _target[i];
            }

            _current[i] = next;
            // 只要收敛到目标即可停止渲染循环。不能把「目标不是 1」当成未收敛，
            // 否则鼠标停在 Dock 上静止时会永远 60FPS 空转（实测空闲 CPU 21%）。
            if (Math.Abs(next - _target[i]) > Epsilon)
            {
                settled = false;
            }
        }

        ComputeOffsets();
        Frame?.Invoke(this, new AnimationFrameEventArgs(
            (double[])_current.Clone(), (double[])_offsets.Clone(), settled));

        if (settled && _tapIndex < 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }
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
