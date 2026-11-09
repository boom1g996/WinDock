using System.IO;
using System.Text;
using System.Threading;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Services;

namespace WinDock.Services;

/// <summary>
/// 离线自检（WD-002 / WD-013）。本机代理不通 nuget.org，无法还原 xunit，
/// 因此用零依赖的进程内断言替代单测：DockHost.exe --selftest。
/// </summary>
public static class SelfTest
{
    public static int Run(ILogService log)
    {
        var lines = new List<string>();
        int passed = 0;
        int failed = 0;

        void Check(string name, bool condition, string detail = "")
        {
            if (condition)
            {
                passed++;
                lines.Add($"PASS  {name}");
            }
            else
            {
                failed++;
                lines.Add($"FAIL  {name}  {detail}");
            }
        }

        string root = Path.Combine(Path.GetTempPath(),
            "WinDock.SelfTest." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            // 1. 越界夹紧 + 非法枚举 + null 子对象
            var config = new DockConfig();
            config.Dock.IconSize = 999;
            config.Appearance.Opacity = 5;
            config.General.TaskbarMode = "bogus";
            config.Pinned = null!;
            var warnings = new List<string>();
            ConfigService.Normalize(config, warnings);
            Check("越界值夹紧 / 非法枚举回退 / null 补齐",
                config.Dock.IconSize == 96
                && Math.Abs(config.Appearance.Opacity - 1.0) < 0.000001
                && config.General.TaskbarMode == "beautify"
                && config.Pinned is not null
                && warnings.Count >= 3,
                $"iconSize={config.Dock.IconSize}, opacity={config.Appearance.Opacity}, warnings={warnings.Count}");

            // 2. 损坏 JSON 回退默认 + 备份坏文件
            string corruptDir = NewCase(root, "corrupt");
            File.WriteAllText(Path.Combine(corruptDir, "config.json"),
                "{ this-is-not-json", new UTF8Encoding(false));
            using (var service = new ConfigService(log, corruptDir))
            {
                Check("损坏配置回退默认值", service.Current.Dock.IconSize == 48);
            }

            Check("损坏配置已备份",
                Directory.GetFiles(corruptDir, "config.corrupt.*.json").Length == 1);

            // 3. 原子写 + 重新读取
            string atomicDir = NewCase(root, "atomic");
            using (var service = new ConfigService(log, atomicDir))
            {
                DockConfig next = service.Current;
                next.Dock.IconSize = 64;
                service.Save(next);
            }

            using (var reread = new ConfigService(log, atomicDir))
            {
                Check("原子写后重新读取", reread.Current.Dock.IconSize == 64,
                    $"实际={reread.Current.Dock.IconSize}");
            }

            // 4. 未知字段忽略（向前兼容）
            string unknownDir = NewCase(root, "unknown");
            File.WriteAllText(Path.Combine(unknownDir, "config.json"),
                "{\"schemaVersion\":1,\"futureField\":123,\"dock\":{\"iconSize\":72}}",
                new UTF8Encoding(false));
            using (var service = new ConfigService(log, unknownDir))
            {
                Check("未知字段忽略", service.Current.Dock.IconSize == 72,
                    $"实际={service.Current.Dock.IconSize}");
            }

            // 5. M2 新增配置字段同样必须夹紧（schemaVersion 保持 1）
            var m2 = new DockConfig();
            m2.Behavior.PreviewHoverDelayMs = 99999;
            m2.Behavior.PreviewMaxWindows = 0;
            m2.Behavior.WatchdogHeartbeatMs = 1;
            m2.Animation.BounceCount = 0;
            m2.Animation.IndicatorFadeMs = 9999;
            ConfigService.Normalize(m2, new List<string>());
            Check("M2 新配置字段越界夹紧",
                m2.Behavior.PreviewHoverDelayMs == 1500
                && m2.Behavior.PreviewMaxWindows == 1
                && m2.Behavior.WatchdogHeartbeatMs == 1000
                && m2.Animation.BounceCount == 1
                && m2.Animation.IndicatorFadeMs == 600,
                $"hover={m2.Behavior.PreviewHoverDelayMs}, maxWin={m2.Behavior.PreviewMaxWindows}, "
                + $"hb={m2.Behavior.WatchdogHeartbeatMs}, bounce={m2.Animation.BounceCount}, "
                + $"fade={m2.Animation.IndicatorFadeMs}");

            // 6. 动画引擎空闲不订阅渲染循环（M1 21% 空转的回归防线）
            using (var engine = new AnimationEngine())
            {
                engine.SetHover(null, 3, 48, 10);
                Check("动画引擎空闲不订阅 Rendering", !engine.IsRunning,
                    $"IsRunning={engine.IsRunning}");
                engine.Bounce(0, 48, 2);
                Check("跳动触发统一时间轴", engine.IsRunning,
                    $"IsRunning={engine.IsRunning}");
                engine.StopBounce();
            }

            // 7. 单一延时调度器：单次触发 + 取消生效，且空闲不常驻回调
            using (var scheduler = new TimerScheduler(new SynchronizationContext()))
            using (var signal = new ManualResetEventSlim(false))
            {
                bool fired = false;
                scheduler.Schedule("selftest.fire", 20, () =>
                {
                    fired = true;
                    signal.Set();
                });
                bool ok = signal.Wait(TimeSpan.FromSeconds(2));
                Check("TimerScheduler 单次触发", ok && fired, $"fired={fired}");

                bool cancelled = false;
                scheduler.Schedule("selftest.cancel", 1000, () => cancelled = true);
                scheduler.Cancel("selftest.cancel");
                Thread.Sleep(60);
                Check("TimerScheduler 取消生效", !cancelled);
            }

            // 8. 中英资源
            Loc.Instance.SetLanguage("zh-CN");
            Check("中文资源可用",
                Loc.Instance.T("AppName") == "WinDock"
                && Loc.Instance.T("Menu_Pin") == "固定到 Dock");
            Loc.Instance.SetLanguage("en");
            Check("英文资源可用",
                Loc.Instance.T("Menu_Pin") == "Pin to Dock",
                $"实际={Loc.Instance.T("Menu_Pin")}");
            Loc.Instance.SetLanguage("zh-CN");
        }
        catch (Exception ex)
        {
            Check("自检执行无异常", false, ex.ToString());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        lines.Add($"RESULT passed={passed} failed={failed}");
        string text = string.Join(Environment.NewLine, lines);
        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "selftest-result.txt"),
                text, new UTF8Encoding(false));
        }
        catch (IOException)
        {
        }

        return failed == 0 ? 0 : 1;
    }

    private static string NewCase(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
