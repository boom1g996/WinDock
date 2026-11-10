using WinDock.Core.Interfaces;
using WinDock.Core.Models;

namespace WinDock.Core.Services;

/// <summary>
/// 自动隐藏状态机（WD-015 / FR-601~603）：
/// - 鼠标离开 Dock 后 HideDelayMs（默认 800ms）延迟滑出；期间移回则取消。
/// - 底边 2px 原生热区进入时立即滑入（D5，无全局钩子）。
/// - 全屏 / 演示模式立即隐藏并禁用热区，退出后 500ms 恢复。
/// - 滑入滑出数值全部来自 IAnimationEngine 单一时间轴，延时全部走 ITimerScheduler。
/// 只依赖 IDockWindowHost 窄接口，不引用 WPF Window（需求 14.2）。
/// </summary>
public sealed class AutoHideController : IAutoHideController
{
    private const string Module = "AutoHide";
    private const string DelayKey = "autohide.delay";
    private const string SlideKey = "autohide.slide";
    private const string RestoreKey = "autohide.fullscreen.restore";
    private const int SlideSettleMs = 220; // 180ms 动画 + 40ms 余量

    private readonly ILogService _log;
    private readonly ITimerScheduler _timers;
    private readonly IAnimationEngine _animation;
    private readonly IFullScreenDetector _fullScreen;
    private readonly IHotZone _hotZone;
    private readonly IDockWindowHost _host;

    private bool _enabled = true;
    private bool _fullScreenHide = true;
    private bool _inFullScreen;
    private int _hideDelayMs = 800;
    private int _slideDurationMs = 180;
    private readonly HashSet<string> _holds = new(StringComparer.Ordinal);
    private bool Held => _holds.Count > 0;
    private bool _disposed;

    public AutoHideController(ILogService log, ITimerScheduler timers,
        IAnimationEngine animation, IFullScreenDetector fullScreen,
        IHotZone hotZone, IDockWindowHost host)
    {
        _log = log;
        _timers = timers;
        _animation = animation;
        _fullScreen = fullScreen;
        _hotZone = hotZone;
        _host = host;

        _host.PointerEntered += OnPointerEntered;
        _host.PointerLeft += OnPointerLeft;
        _hotZone.Entered += OnHotZoneEntered;
        _fullScreen.StateChanged += OnFullScreenChanged;
    }

    public event EventHandler<bool>? VisibilityChanged;

    /// <summary>目标状态：true 表示 Dock 已隐藏（含全屏强制隐藏）。</summary>
    public bool IsHidden { get; private set; }

    public void ApplyConfig(DockConfig config)
    {
        _enabled = config.Behavior.AutoHide;
        _hideDelayMs = config.Behavior.HideDelayMs;
        _fullScreenHide = config.Behavior.FullScreenHide;
        // 配置关掉「全屏时隐藏」后，全屏态不应对 Dock 产生任何影响。
        // 旧实现只在 OnFullScreenChanged 里判 _fullScreenHide，但 _inFullScreen 已经被置 true，
        // 于是下面 `!_enabled && !_inFullScreen` 不成立 → 关掉自动隐藏也显示不出来（实机 bug）。
        _inFullScreen = _fullScreenHide && _fullScreen.IsFullScreen;
        _slideDurationMs = 180;
        _animation.ConfigureMotion(config.Animation.IndicatorFadeMs, _slideDurationMs);
        _hotZone.Apply(config.Behavior.EdgeHotzonePx);

        if (!_enabled && IsHidden && !_inFullScreen)
        {
            Show(); // 用户关掉自动隐藏：立即恢复常驻
            return;
        }

        SyncHotZone();
        if (_enabled && !Held && !IsHidden && !_inFullScreen && !_host.IsPointerOverDock)
        {
            // FR-601 默认开：冷启动时鼠标通常不在 Dock 上，不能等一次
            // MouseEnter/MouseLeave 才生效，否则自动隐藏形同虚设。
            _timers.Schedule(DelayKey, Math.Max(0, _hideDelayMs), () => Hide());
        }

        _log.Info(Module, $"自动隐藏={_enabled}, 延迟={_hideDelayMs}ms, 全屏隐藏={_fullScreenHide}");
    }

    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        _timers.Cancel(DelayKey);
        _timers.Cancel(RestoreKey);
        if (!IsHidden)
        {
            _host.SetVisible(true);
            _host.Reposition();
            _host.SetInteractive(true);
            SyncHotZone();
            return;
        }

        IsHidden = false;
        _host.SetVisible(true);
        _host.Reposition(); // 折叠态 2×2 残留坐标必须重算，否则唤回后 Dock 错位看不到
        _host.SetInteractive(false); // 滑入途中不接收点击，避免误触
        _animation.Slide(0);
        _timers.Schedule(SlideKey, SlideSettleMs, () => CompleteSlide(hidden: false));
        VisibilityChanged?.Invoke(this, true);
    }

    public void Hide(bool force = false)
    {
        if (_disposed || IsHidden || (Held && !force))
        {
            return; // 浮层交互期间抑制自动隐藏；全屏强制隐藏不受影响
        }

        if (!force && _host.IsPointerOverDock)
        {
            // 鼠标又回到了 Dock 上：延后重试，不做隐藏。
            _timers.Schedule(DelayKey, Math.Max(50, _hideDelayMs), () => Hide());
            return;
        }

        IsHidden = true;
        _timers.Cancel(DelayKey);
        _host.SetInteractive(false);
        _animation.Slide(_host.HiddenDistance);
        _timers.Schedule(SlideKey, SlideSettleMs, () => CompleteSlide(hidden: true));
        VisibilityChanged?.Invoke(this, false);
    }

    public void Toggle()
    {
        if (IsHidden)
        {
            Show();
        }
        else
        {
            Hide(force: true);
        }
    }

    public void SetHeld(string reason, bool held)
    {
        if (_disposed || string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        bool before = Held;
        if (held)
        {
            _holds.Add(reason);
        }
        else
        {
            _holds.Remove(reason);
        }

        bool after = Held;
        if (before == after)
        {
            return; // 引用计数未跨过 0 边界：不打断其他仍打开的浮层
        }

        _timers.Cancel(DelayKey);
        if (after)
        {
            if (!_inFullScreen)
            {
                Show(); // 浮层打开时保证 Dock 常驻可见
            }
            // 仍处全屏态：保持隐藏（全屏期间 OnFullScreenChanged 不会因 Held 改变可见性）
        }
        else if (_inFullScreen)
        {
            // 最后一个浮层关闭且仍处于全屏 / 演示态：全屏优先级高于自动隐藏，立即隐藏。
            Hide(force: true);
            _host.SetVisible(false);
        }
        else if (_enabled && !IsHidden && !_host.IsPointerOverDock)
        {
            // 最后一个浮层关闭且鼠标仍在别处：重新按 HideDelayMs 计时，不立即滑出。
            _timers.Schedule(DelayKey, Math.Max(0, _hideDelayMs), () => Hide());
        }

        _log.Info(Module, after
            ? $"浮层打开（{reason}）：暂停自动隐藏"
            : "浮层全部关闭：恢复自动隐藏");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.PointerEntered -= OnPointerEntered;
        _host.PointerLeft -= OnPointerLeft;
        _hotZone.Entered -= OnHotZoneEntered;
        _fullScreen.StateChanged -= OnFullScreenChanged;
        _timers.Cancel(DelayKey);
        _timers.Cancel(SlideKey);
        _timers.Cancel(RestoreKey);
    }

    private void CompleteSlide(bool hidden)
    {
        if (hidden)
        {
            _host.SetVisible(false);
            SyncHotZone();
        }
        else
        {
            _host.SetVisible(true);
            _host.SetInteractive(true);
            SyncHotZone();
        }
    }

    private void SyncHotZone()
    {
        if (_enabled && IsHidden && !_inFullScreen)
        {
            _hotZone.Enable();
        }
        else
        {
            _hotZone.Disable();
        }
    }

    private void OnPointerEntered(object? sender, EventArgs args)
    {
        _timers.Cancel(DelayKey);
        if (IsHidden && !_inFullScreen)
        {
            Show();
        }
    }

    private void OnPointerLeft(object? sender, EventArgs args)
    {
        if (!_enabled || IsHidden || _inFullScreen || Held)
        {
            return;
        }

        _timers.Schedule(DelayKey, Math.Max(0, _hideDelayMs), () => Hide());
    }

    private void OnHotZoneEntered(object? sender, EventArgs args)
    {
        // 全屏期间热区已被禁用，这里再挡一层避免边缘事件竞态。
        if (_inFullScreen)
        {
            return;
        }

        Show();
    }

    private void OnFullScreenChanged(object? sender, bool full)
    {
        // 「全屏隐藏」关掉时，全屏态不影响 Dock：不隐藏、不禁用热区、不拦 Show。
        _inFullScreen = full && _fullScreenHide;
        if (!_fullScreenHide)
        {
            return;
        }

        if (full)
        {
            // FR-603「立即隐藏」：独占全屏 / 演示态下 Dock 应让位。但 WinDock 自己的
            // UI 表面（设置 / 引导 / 预览）打开时，用户正在操作 Dock 而非看全屏内容，
            // 不隐藏——否则「打开设置时 Dock 图标消失」之类体感会复现（此前只抑制了
            // 自动隐藏、漏了全屏隐藏）。等最后一个浮层关闭后，SetHeld 释放路径会重新
            // 判定全屏并隐藏。
            if (Held)
            {
                _log.Info(Module, "全屏态但浮层打开：暂不隐藏，等浮层关闭");
                _timers.Cancel(RestoreKey);
                _hotZone.Disable();
                return;
            }

            _timers.Cancel(RestoreKey);
            _hotZone.Disable();
            Hide(force: true);
            // FR-603「立即隐藏」：不等 180ms 动画结束，先把窗口移出可视区。
            _host.SetVisible(false);
            _log.Info(Module, "全屏态：Dock 已立即隐藏，热区禁用");
        }
        else
        {
            _timers.Schedule(RestoreKey, 500, () =>
            {
                if (_inFullScreen)
                {
                    return;
                }

                Show();
            });
        }
    }
}
