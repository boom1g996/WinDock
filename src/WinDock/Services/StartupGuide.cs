using WinDock.Core.Interfaces;
using WinDock.Core.Models;

namespace WinDock.Services;

/// <summary>
/// WD-017 首次启动引导的服务门面：判定是否首次运行、创建引导窗口并在
/// 完成或意外关闭时只回调一次。UI 属于 WinDock 层，Core 只持有
/// <see cref="IStartupGuide"/> 契约（需求 14.2）。
/// </summary>
public sealed class StartupGuide : IStartupGuide
{
    private readonly IConfigService _config;
    private readonly ILogService _log;
    private bool _visible;

    public StartupGuide(IConfigService config, ILogService log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>纯判定，离线自检可直接断言，不需要创建窗口。</summary>
    public static bool NeedsGuide(DockConfig config) => !config.General.FirstRunCompleted;

    public bool IsVisible => _visible;

    public bool ShouldShow(DockConfig config) => NeedsGuide(config);

    public void Show(Action onCompleted)
    {
        if (_visible)
        {
            return; // 托盘重复点击不弹第二个引导窗口
        }

        GuideWindow window;
        try
        {
            window = new GuideWindow(_config, _log);
        }
        catch (Exception ex)
        {
            _log.Error("Guide", "创建引导窗口失败", ex);
            onCompleted(); // 释放自动隐藏 Hold，避免被永久抑制
            return;
        }

        _visible = true;
        bool finished = false;
        void Finish()
        {
            if (finished)
            {
                return;
            }

            finished = true;
            _visible = false;
            onCompleted();
        }

        window.Completed += (_, _) => Finish();
        window.Closed += (_, _) => Finish();
        window.Show();
        window.Activate();
        _log.Info("Guide", "首次启动引导已显示");
    }
}
