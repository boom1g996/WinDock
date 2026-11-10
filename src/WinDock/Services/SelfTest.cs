using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Native;
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

            // 8. WD-014：缩略图几何（纯函数，保持源宽高比并居中留边）
            Rect box = new(0, 0, 160, 90);
            Rect fitted = PreviewService.FitRect(box, 1920, 1080);
            Check("缩略图按源比例铺满目标",
                Math.Abs(fitted.Width - 160) < 0.001
                && Math.Abs(fitted.Height - 90) < 0.001
                && Math.Abs(fitted.Left) < 0.001
                && Math.Abs(fitted.Top) < 0.001,
                $"rect={fitted}");
            Rect tall = PreviewService.FitRect(box, 600, 1200);
            Check("竖屏源不拉伸且居中留边",
                Math.Abs(tall.Width - 45) < 0.001
                && Math.Abs(tall.Height - 90) < 0.001
                && Math.Abs(tall.Left - 57.5) < 0.001,
                $"rect={tall}");

            // 9. WD-014：上限夹紧、无宿主时不注册、Hide 清空条目
            using (var preview = new PreviewService(log))
            {
                var clamp = new DockConfig();
                clamp.Behavior.PreviewMaxWindows = 99;
                ConfigService.Normalize(clamp, new List<string>());
                preview.ApplyConfig(clamp);
                Check("缩略图上限夹紧到 6", preview.MaxThumbnails == 6,
                    $"实际={preview.MaxThumbnails}");

                var targets = Enumerable.Range(1, 9)
                    .Select(i => new WindowPreviewTarget(new IntPtr(i), $"W{i}", false))
                    .ToList();
                int registered = preview.Show(IntPtr.Zero, targets);
                Check("无宿主 HWND 不注册且条目截断到上限",
                    registered == 0 && preview.ActiveCount == 6,
                    $"registered={registered}, active={preview.ActiveCount}");
                preview.Hide();
                Check("Hide 清空全部缩略图条目", preview.ActiveCount == 0,
                    $"active={preview.ActiveCount}");
            }

            // 10. WD-017：首次引导判定与标记持久化
            Check("默认配置需要显示首次引导",
                StartupGuide.NeedsGuide(new DockConfig()));
            string guideDir = NewCase(root, "guide");
            using (var service = new ConfigService(log, guideDir))
            {
                DockConfig done = service.Current;
                done.General.FirstRunCompleted = true;
                service.Save(done);
            }

            using (var reread = new ConfigService(log, guideDir))
            {
                Check("引导完成标记持久化",
                    !StartupGuide.NeedsGuide(reread.Current));
            }

            // 11. 新增本地化键中英齐备
            Loc.Instance.SetLanguage("zh-CN");
            Check("预览/引导中文资源齐备",
                Loc.Instance.T("Guide_S1_Title") == "欢迎使用 WinDock"
                && Loc.Instance.T("Preview_Minimized") == "已最小化");
            Loc.Instance.SetLanguage("en");
            Check("预览/引导英文资源齐备",
                Loc.Instance.T("Guide_S1_Title") == "Welcome to WinDock"
                && Loc.Instance.T("Preview_Minimized") == "Minimized",
                $"实际={Loc.Instance.T("Guide_S1_Title")}");
            Loc.Instance.SetLanguage("zh-CN");

            // 12. 中英资源
            Loc.Instance.SetLanguage("zh-CN");
            Check("中文资源可用",
                Loc.Instance.T("AppName") == "WinDock"
                && Loc.Instance.T("Menu_Pin") == "固定到 Dock");
            Loc.Instance.SetLanguage("en");
            Check("英文资源可用",
                Loc.Instance.T("Menu_Pin") == "Pin to Dock",
                $"实际={Loc.Instance.T("Menu_Pin")}");
            Loc.Instance.SetLanguage("zh-CN");

            // 13~15. WD-016：拖拽排序、拖出移除、运行项转固定、重启后顺序保持（AC-06）
            string dragDir = NewCase(root, "drag");
            using (var service = new ConfigService(log, dragDir))
            {
                DockConfig dragConfig = service.Current;
                dragConfig.Pinned.Clear();
                foreach (string name in new[] { "A", "B", "C" })
                {
                    dragConfig.Pinned.Add(new PinnedItem
                    {
                        Id = name.ToLowerInvariant(),
                        Path = $@"C:\Apps\{name}.exe",
                        Name = name,
                    });
                }

                service.Save(dragConfig);
                var drag = new DockDragService(service, log);
                drag.BeginDrag();
                Check("拖拽进入状态", drag.IsDragging);
                drag.Reorder(0, 3); // 插入位置允许等于 Count：A 追加到 C 之后
                drag.EndDrag();
                Check("固定项按落点重排",
                    string.Join(",", service.Current.Pinned.Select(p => p.Name)) == "B,C,A",
                    $"实际={string.Join(",", service.Current.Pinned.Select(p => p.Name))}");
            }

            using (var reread = new ConfigService(log, dragDir))
            {
                Check("AC-06 顺序重启后保持",
                    string.Join(",", reread.Current.Pinned.Select(p => p.Name)) == "B,C,A");
            }

            using (var service = new ConfigService(log, NewCase(root, "drag-remove")))
            {
                var drag = new DockDragService(service, log);
                service.Current.Pinned.Add(new PinnedItem { Id = "a", Path = @"C:\A.exe", Name = "A" });
                service.Current.Pinned.Add(new PinnedItem { Id = "b", Path = @"C:\B.exe", Name = "B" });
                drag.Remove(0);
                drag.Reorder(9, 0);   // 越界必须忽略
                drag.Remove(-1);
                drag.Pin(0, @"C:\B.exe", "B alias"); // 同路径已固定：不产生重复项
                Check("拖出移除 / 越界忽略 / 同路径不重复",
                    service.Current.Pinned.Count == 1
                    && service.Current.Pinned[0].Path == @"C:\B.exe",
                    $"count={service.Current.Pinned.Count}");
                drag.Pin(0, @"C:\D.exe", "D");
                Check("运行项拖入固定区按落点插入",
                    service.Current.Pinned.Count == 2
                    && service.Current.Pinned[0].Name == "D",
                    $"实际={string.Join(",", service.Current.Pinned.Select(p => p.Name))}");
            }

            // 16~19. WD-020：看门狗协议、恢复事件与缺失 exe 的安全降级
            Check("看门狗阈值夹紧与心跳间隔",
                WatchdogProtocol.ClampTimeout(50) == 1000
                && WatchdogProtocol.ClampTimeout(999999) == 60000
                // WD-035：除数 1/2 → 1/3（5s → 3.3s），给「宿主存活却判失联」留丢帧余量
                && WatchdogProtocol.HeartbeatIntervalMs(10000) == 3333
                && WatchdogProtocol.HeartbeatIntervalMs(1000) == 333);
            Check("心跳 / 正常退出帧协议",
                WatchdogProtocol.IsHeartbeat("H")
                && WatchdogProtocol.IsShutdown("S")
                && !WatchdogProtocol.IsHeartbeat("h")
                && !WatchdogProtocol.IsShutdown("X"));
            Check("管道名按宿主 PID 隔离",
                WatchdogProtocol.PipeName(1) != WatchdogProtocol.PipeName(2)
                && WatchdogProtocol.PipeName(42).EndsWith("42", StringComparison.Ordinal));

            WatchdogRecoveryEvent recovery =
                WatchdogProtocol.Create(1234, "host-exited", taskbarRestored: true, snapshotPresent: false);
            WatchdogRecoveryEvent? decoded =
                WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(recovery));
            Check("恢复事件序列化往返",
                decoded is not null && decoded.Pid == 1234
                && decoded.Reason == "host-exited" && decoded.TaskbarRestored
                && !decoded.SnapshotPresent);
            Check("损坏恢复事件不抛异常",
                WatchdogProtocol.Deserialize("{bad-json") is null);

            using (var scheduler = new TimerScheduler(new SynchronizationContext()))
            using (var watchdog = new WatchdogService(log, scheduler,
                executable: Path.Combine(root, "missing-watchdog.exe")))
            {
                watchdog.ApplyConfig(new DockConfig());
                Check("看门狗 exe 缺失时安全降级", !watchdog.IsRunning);
                watchdog.Heartbeat(); // 未连接时不得抛异常
            }

            // 20. 恢复提示中英资源
            Loc.Instance.SetLanguage("zh-CN");
            Check("恢复提示中文资源",
                Loc.Instance.T("Tray_RecoveryTitle") == "WinDock 上次异常退出");
            Loc.Instance.SetLanguage("en");
            Check("恢复提示英文资源",
                Loc.Instance.T("Tray_RecoveryTitle") == "WinDock exited unexpectedly");
            Loc.Instance.SetLanguage("zh-CN");

            // 21. WD-033 本地打点：计数/耗时聚合、Mark 不抛异常
            using (var metrics = new MetricsService(log))
            {
                metrics.Count("test.count");
                metrics.Count("test.count", 2);
                metrics.Record("test.record", 4.5);
                metrics.Record("test.record", 1.5);
                IReadOnlyDictionary<string, MetricsStat> snapshot = metrics.Snapshot();
                Check("打点计数聚合",
                    snapshot.TryGetValue("test.count", out MetricsStat c) && c.Count == 3,
                    $"实际 count={c.Count}");
                Check("打点耗时聚合（n/total/max）",
                    snapshot.TryGetValue("test.record", out MetricsStat r)
                    && r.Count == 2 && Math.Abs(r.TotalMs - 6.0) < 0.01
                    && Math.Abs(r.MaxMs - 4.5) < 0.01,
                    $"实际 n={r.Count}, total={r.TotalMs}, max={r.MaxMs}");
                metrics.Mark("selftest.mark"); // Mark 落 Info 日志，不抛异常即通过
                Check("Mark 里程碑打点不抛异常", true);
            }

            // 22. 配置开关字段存在且默认开启
            Check("MetricsEnabled 默认开启", new DockConfig().Behavior.MetricsEnabled);

            // 23. WD-037 / AC-05：CloseWindow 与 CloseAll 走同一 WM_CLOSE 路径
            {
                var launcher = new AppLauncher(log);
                int single = 0, batch = 0;
                launcher.PostCloseHook = _ => { single++; return true; };
                Check("CloseWindow 投递单条 WM_CLOSE",
                    launcher.CloseWindow(new AppWindowInfo { Handle = (IntPtr)0x1111, Title = "w1" })
                    && single == 1, $"hook 调用 {single} 次");

                launcher.PostCloseHook = _ => { batch++; return true; };
                var group = new AppWindowGroup { Key = "g", DisplayName = "g" };
                group.Windows.Add(new AppWindowInfo { Handle = (IntPtr)0x21, Title = "a" });
                group.Windows.Add(new AppWindowInfo { Handle = (IntPtr)0x22, Title = "b" });
                group.Windows.Add(new AppWindowInfo { Handle = (IntPtr)0x23, Title = "c" });
                launcher.CloseAll(group);
                Check("CloseAll 复用同一路径（3 窗口 = 3 次投递）",
                    batch == 3 && batch == group.Windows.Count,
                    $"hook 调用 {batch} 次 / 期望 3");

                int failures = 0;
                launcher.PostCloseHook = _ => { failures++; return false; };
                Check("投递失败返回 false（窗口已退出）",
                    !launcher.CloseWindow(new AppWindowInfo { Handle = (IntPtr)0x33 })
                    && failures == 1);
            }

            // 24. WD-038 / AC-21：FrameRateCounter 帧率统计（纯逻辑）
            {
                var smooth = new FrameRateCounter();
                for (int i = 0; i < 120; i++)
                {
                    smooth.AddFrame(i * 17); // 17ms ≈ 58.8 FPS，无丢帧
                }

                FrameRateSnapshot snap = smooth.Snapshot();
                Check("17ms 帧序列 → 平均 FPS ≈ 58.8 且零丢帧",
                    Math.Abs(snap.WindowAvg - 58.82) < 2.0 && snap.Dropped == 0,
                    $"avg={snap.WindowAvg}, dropped={snap.Dropped}");

                var choppy = new FrameRateCounter();
                for (int i = 0; i < 60; i++)
                {
                    choppy.AddFrame(i * 33); // 33ms ≈ 30.3 FPS，每帧间隔都超丢帧阈值
                }

                FrameRateSnapshot bad = choppy.Snapshot();
                Check("33ms 帧序列 → 平均 FPS ≈ 30 且逐帧计丢帧",
                    Math.Abs(bad.WindowAvg - 30.3) < 2.0 && bad.Dropped == 59,
                    $"avg={bad.WindowAvg}, dropped={bad.Dropped}");
                Check("最近 1 秒帧数与序列一致（33ms → ~30 帧）",
                    Math.Abs(bad.Last1s - 30) <= 1, $"last1s={bad.Last1s}");
                Check("活跃突发统计（AC-21 口径）：连续序列成 1 个突发、active=window",
                    bad.Bursts == 1 && Math.Abs(bad.ActiveAvg - bad.WindowAvg) < 0.5,
                    $"bursts={bad.Bursts}, active={bad.ActiveAvg}");

                // 空闲分隔：两段 60FPS 动画之间隔 2s，时间平均被稀释但 active 不受影响。
                var gapped = new FrameRateCounter();
                for (int i = 0; i < 60; i++)
                {
                    gapped.AddFrame(i * 17);
                }

                for (int i = 0; i < 30; i++)
                {
                    gapped.AddFrame(2000 + i * 17);
                }

                FrameRateSnapshot sep = gapped.Snapshot();
                Check("空闲分隔不计丢帧、active 突发独立统计",
                    sep.Dropped == 0 && sep.Bursts == 2
                    && Math.Abs(sep.ActiveAvg - 58.82) < 2.0
                    && sep.WindowAvg < 45.0,
                    $"dropped={sep.Dropped}, bursts={sep.Bursts}, " +
                    $"active={sep.ActiveAvg}, window={sep.WindowAvg}");
            }

            // 25. M3/M4：预设主题 / 配置导入导出 / 多显示器解析 / 系统区与拖入默认值
            {
                Check("预设标识齐全且都在校验表内（custom + 5 套）",
                    ThemePresets.Names.Length == 5
                    && ThemePresets.Names.All(n => ConfigRanges.Presets.Contains(n))
                    && ConfigRanges.Presets.Contains("custom"),
                    $"names={string.Join(",", ThemePresets.Names)}");

                var themed = new DockConfig();
                themed.Behavior.AutoHide = false;
                themed.General.Language = "en";
                ThemePresets.Apply(themed, "cyber");
                Check("应用预设写入外观/布局/动效并标记 preset",
                    themed.Appearance.Preset == "cyber"
                    && themed.Dock.IconShape == "circle"
                    && themed.Appearance.AccentColor == "#FF00E5FF"
                    && themed.Appearance.Reflection.Enabled
                    && Math.Abs(themed.Animation.MagnifyScale - 2.0) < 0.000001,
                    $"preset={themed.Appearance.Preset}, shape={themed.Dock.IconShape}, " +
                    $"scale={themed.Animation.MagnifyScale}");
                Check("应用预设不覆盖行为/通用偏好（FR-610）",
                    !themed.Behavior.AutoHide && themed.General.Language == "en");

                var bogusPreset = new DockConfig();
                bogusPreset.Appearance.Preset = "no-such-preset";
                ConfigService.Normalize(bogusPreset, new List<string>());
                Check("未知预设名回退 custom", bogusPreset.Appearance.Preset == "custom",
                    $"实际={bogusPreset.Appearance.Preset}");

                // 导出 → 导入往返（WD-031：不丢字段；非法文件给可分类异常）
                string exportDir = NewCase(root, "export");
                string exportPath = Path.Combine(exportDir, "nested", "WinDock-config.json");
                using (var service = new ConfigService(log, exportDir))
                {
                    DockConfig cur = service.Current;
                    cur.Dock.IconSize = 72;
                    cur.Appearance.Preset = "mono";
                    cur.Behavior.ShowSystemArea = true;
                    service.Save(cur);
                    service.ExportTo(exportPath); // 目录不存在时自动创建
                }

                Check("导出生成 JSON 且含关键字段",
                    File.Exists(exportPath)
                    && File.ReadAllText(exportPath).Contains("\"showSystemArea\": true"),
                    exportPath);

                using (var target = new ConfigService(log, NewCase(root, "import")))
                {
                    DockConfig imported = target.ImportFrom(exportPath);
                    Check("导入往返：图标尺寸/预设/系统区开关一致",
                        imported.Dock.IconSize == 72
                        && imported.Appearance.Preset == "mono"
                        && imported.Behavior.ShowSystemArea,
                        $"size={imported.Dock.IconSize}, preset={imported.Appearance.Preset}");
                    Check("导入结果已作为新配置源保存",
                        target.Current.Dock.IconSize == 72,
                        $"实际={target.Current.Dock.IconSize}");
                }

                string badJson = Path.Combine(exportDir, "bad.json");
                File.WriteAllText(badJson, "{ not json", new UTF8Encoding(false));
                var importFailed = false;
                using (var target = new ConfigService(log, NewCase(root, "import-bad")))
                {
                    try
                    {
                        target.ImportFrom(badJson);
                    }
                    catch (InvalidDataException)
                    {
                        importFailed = true;
                    }
                }

                Check("导入非法 JSON 抛 InvalidDataException（供 UI 分类提示）", importFailed);

                // 多显示器 / DPI（WD-021）
                IReadOnlyList<DisplayMonitor> monitors = MonitorService.GetMonitors();
                DisplayMonitor? primaryMonitor = monitors.FirstOrDefault(m => m.IsPrimary);
                Check("枚举到显示器且主屏工作区/DPI 合法",
                    monitors.Count >= 1 && primaryMonitor is not null
                    && primaryMonitor.ScaleX > 0.5 && primaryMonitor.WorkAreaDip.Width > 100,
                    $"count={monitors.Count}, primaryDpi={primaryMonitor?.DpiX}, " +
                    $"work={primaryMonitor?.WorkAreaDip.Width}");

                DipRect fallback = MonitorService.ResolveWorkArea("99", out bool fbPrimary);
                DipRect primary = MonitorService.ResolveWorkArea("primary", out bool pPrimary);
                Check("越界 monitor id 回退主屏工作区",
                    fbPrimary && pPrimary
                    && Math.Abs(fallback.Left - primary.Left) < 0.5
                    && Math.Abs(fallback.Width - primary.Width) < 0.5,
                    $"fallback={fallback.Left}/{fallback.Width}, primary={primary.Left}/{primary.Width}");

                // 系统区只读查询（WD-026）：回收站为空/满都不得抛异常
                var recycleOk = true;
                try
                {
                    _ = NativeMethods.IsRecycleBinEmpty();
                }
                catch (Exception)
                {
                    recycleOk = false;
                }

                Check("查询回收站状态不抛异常", recycleOk);

                var defaults = new DockConfig();
                Check("系统区默认关闭、子项/拖入/最小化反馈默认开启",
                    !defaults.Behavior.ShowSystemArea
                    && defaults.Behavior.ShowRecycleBin
                    && defaults.Behavior.ShowTaskView
                    && defaults.Behavior.ShowShowDesktop
                    && defaults.Behavior.DragDropAddEnabled
                    && defaults.Behavior.MinimizeFeedbackEnabled);
            }
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
