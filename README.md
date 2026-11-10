# WinDock

把 Windows 11 任务栏改造成 macOS Dock 风格的桌面应用：悬浮胶囊、邻域放大、运行指示点、窗口缩略图预览、首次启动引导、自动隐藏、拖拽排序、独立进程看门狗与多屏适配。

> **当前阶段**：M1 MVP（WD-001~013）、**M2 八批**（WD-014~020、WD-033~039，见下方文档）、**M3 七项**（WD-021~027）与 **M4 五项**（WD-028~032）**均已实施落地**；此后又按「先补齐功能、再打磨细节」的要求做了 **两轮需求逐条复核**，把「有字段无实现 / 有章节无代码」的条目一并补齐（见 [M3-M4 变更说明 §6](docs/M3-M4-变更说明与已知问题.md)）。当前交付：Release 构建 0 警告 0 错误、离线自检 **75/75**。
>
> **两轮复核新增**：FR-101 倒影+底部高光、FR-102 指示样式（点/宽条/三条）、FR-203 注意跳动（`RegisterShellHookWindow` + `HSHELL_FLASH`）、FR-205 悬停浮起、FR-304 常驻窗口切换面板、FR-402「+N」、6.7 每屏一条 Dock、6.10 全量可调项、6.11 UWP AUMID 图标与低分辨率提示、6.13 模式 B 精简共存（HKCU 用户项 + 快照还原）与模式 A 托盘溢出提示、8.3 RDP/虚拟机/高对比度回退、8.4 名称常显 + AutomationPeer、10 UI 错误态与面板 14px 圆角、R2 性能模式、R5 管理员重启、R9 第三方美化工具冲突检测、5.2 气泡「一键恢复」、AC-10 恢复即退出、6.15 卸载还原脚本。
>
> **M3/M4 新增能力**（细节见 [M3-M4 变更说明与已知问题](docs/M3-M4-变更说明与已知问题.md)）：6.10 五套预设主题 + 强调色、6.1 背景毛玻璃（`DwmExtendFrameIntoClientArea` 整窗玻璃 + 圆角）、6.7 多显示器定位（`EnumDisplayMonitors` + Per-Monitor DPI 换算）、6.2 图标清晰度（按 DPI × 放大倍率请求位图）、6.9 托盘/设置项补全（模式切换 A/B/C 即时生效、开机自启、检查更新）、6.5 系统区（回收站/任务视图/显示桌面）、6.6 拖入添加、6.4 Jump List、6.3 最小化吸入反馈、6.8 配置导入导出、6.11 屏分辨率变化重排（`WM_DISPLAYCHANGE`）。
>
> **性能如实说明**：CPU——需求 8.1 口径为「任务管理器 5 分钟平均」：长稳 2.41h 均值 **0.212%**、安静样本窗口达标率 93.8%（`docs/M2-stability.summary.json`）；30s 单次 0.573% 属 15.6ms 记账粒度放大，不适合判 8.1；关看门狗对照 0.104%。内存——工作集 ~200MB、私有字节预热后 148~191MB（净漂移 -34MB），≤120MB 任一口径未达标（WPF D3D 结构性开销，K-01/K-14 转产品决策）。帧率——AC-21 活跃口径 **57.18 FPS ≥55 达标**（P5≈32 偶发掉帧如实登记）。长稳按用户决策以 **2.41h 部分实证**收口（0 崩溃 0 恢复 0 错误），跨天/睡眠唤醒场景留 M3 验收补证。详见 [M2 变更说明与已知问题](docs/M2-变更说明与已知问题.md) 与 [M3 性能与兼容性矩阵](docs/M3-性能与兼容性矩阵.md)。

## 项目管理

| 入口 | 地址 |
| --- | --- |
| 看板 Projects v2 #1「W&dock」 | https://github.com/users/boom1g996/projects/1/views/1 |
| Issues / 里程碑 | https://github.com/boom1g996/WinDock/issues · https://github.com/boom1g996/WinDock/milestones |
| 项目管理手册 | [docs/GITHUB-项目管理.md](docs/GITHUB-项目管理.md) |

任务清单以 [docs/BACKLOG.md](docs/BACKLOG.md) 为唯一导入源，由 `tools/create_github_project.py` 幂等同步到 Issues 与看板。

## 文档

| 文件 | 内容 |
| --- | --- |
| [M0 技术预研报告](docs/M0-技术预研报告.md) | 系统能力实测、10+5 项可行性判定、阻塞项、GitHub 权限核查、SDK 验证 |
| [M1 设计说明与任务拆解](docs/M1-设计说明与任务拆解.md) | 架构设计、13 条 M1 任务、验收映射、6 条默认值（已确认采纳） |
| [M1 变更说明与已知问题](docs/M1-变更说明与已知问题.md) | 交付物、验证结果、性能实测、已知问题列表与 M2 建议 |
| [M2 设计说明与任务拆解](docs/M2-设计说明与任务拆解.md) | 10 条设计决策（含实机修订）、新增接口边界、WD-014~036 任务与验收映射、6 条默认值（**已确认**）、六批实施进度与风险降级 |
| [M2 变更说明与已知问题](docs/M2-变更说明与已知问题.md) | 阶段性交付物、第六批变更清单、验证结果、K-01~K-20 已知问题、长期结论与下一步 |
| [M2 指标与验收口径待确认清单](docs/M2-指标与验收口径待确认清单.md) | 待产品/评审拍板的 6 条口径（CPU 判定窗口、内存阈值、AC-05 / AC-12 / AC-21 缺口、AC-11 张力），每条含原文、实测、建议 |
| [M3-M4 变更说明与已知问题](docs/M3-M4-变更说明与已知问题.md) | M3 七项 + M4 五项变更表（含实现位置与验收映射）、验证结果、已知问题 M3-K1~K6 |
| [M3 性能与兼容性矩阵](docs/M3-性能与兼容性矩阵.md) | 需求 8.1 十项指标实测口径、8.3 兼容性矩阵（DPI / RDP / 高对比度 / VM 回退）、8.2 稳定性、8.4 可访问性、8.5 i18n、M3 回归清单 |
| [M2-probe-raw.json](docs/M2-probe-raw.json) | M2 能力预研原始数据（DWM / QUNS / Flash / HotKey / 热区） |
| [M2-smoke-batch2.json](docs/M2-smoke-batch2.json) | 第二批实机冒烟：自动隐藏、热区恢复、稳态 CPU 分段 |
| [M2-smoke-batch3.json](docs/M2-smoke-batch3.json) | 第三批功能冒烟：引导窗口、预览面板、稳态 CPU；`-initial` 保留回环回归现场，`-idle-trace` 为 30s 分段复采 |
| [M2-smoke-batch4.json](docs/M2-smoke-batch4.json) | 第四批功能冒烟：拖拽顺序落盘、预览开合、看门狗强杀恢复与正常退出 S 帧握手 |
| [M2-smoke-batch4-idle.json](docs/M2-smoke-batch4-idle.json) | 第四批 30s 纯稳态 CPU 分段与内存 |
| [M2-smoke-batch5*.json](docs/M2-smoke-batch5-full.json) | 第五批：全功能冒烟 / 30s 稳态（0.417%）/ 关看门狗对照（0.104% 达标）/ 锁屏环境对照 |
| [M2-smoke-batch6-k17.json](docs/M2-smoke-batch6-k17.json) | 第六批：解锁后全功能冒烟（拖拽 / 预览 / 看门狗 PASS，K-17 关闭） |
| [M2-smoke-batch6-idle.json](docs/M2-smoke-batch6-idle.json) | 第六批 30s 纯稳态（3.3s 心跳，0.573% / 199MB） |
| [M2-stability.jsonl](docs/M2-stability.jsonl) | WD-035 长稳采样（60s/点，逐行 JSON；`--summary` 生成同名 `.summary.json`：5 分钟滚动均值 + 达标窗口占比 + 宿主 pid 分段 + GDI/USER 漂移） |
| [M2-smoke-batch7-close.json](docs/M2-smoke-batch7-close.json) | 第八批：预览面板关闭按钮全链路（悬停→点击→WM_CLOSE→目标退出→面板收缩） |
| [M2-smoke-batch7-fps.json](docs/M2-smoke-batch7-fps.json) | 第八批：60s 悬停帧率（活跃突发口径 57.18FPS；P5/掉帧如实登记） |
| [M2-smoke-batch7-dpi.json](docs/M2-smoke-batch7-dpi.json) | 第八批：DPI 审计（感知上下文/坐标一致性/图标命中/热区，125%） |
| [M1-smoke-result.json](docs/M1-smoke-result.json) | M1 交付冒烟原始结果（窗口 / CPU / 内存 / 日志错误） |
| [BACKLOG](docs/BACKLOG.md) | 5 里程碑 / 39 议题（含 M2 新增 4 条）/ 17 标签 |
| [M0-probe-raw.json](docs/M0-probe-raw.json) | 只读系统探针原始输出 |

需求唯一真源：桌面 `Win11Dock/需求文档.md` v1.0（未纳入本仓库）。

## 技术栈

- **.NET 10 LTS + WPF**。需求文档 4.2 原推荐 .NET 8，但 .NET 8 已于 2026-11-10 终止官方支持，故改用支持至 2028-11-14 的 .NET 10；WPF 用法完全一致。SDK 10.0.403 已验证可构建（0 警告 0 错误）。
- 实际 TFM 为 `net10.0-windows`（非设计说明中的 `net10.0-windows10.0.22000.0`）：离线 NuGet 源清空后带 Windows SDK 版本号的 TFM 需要还原 `Microsoft.Windows.SDK.NET.Ref`，故降级为纯 Win32 P/Invoke 方案。
- 系统互操作靠 P/Invoke，全部声明集中在 `WinDock.Core/Native/NativeMethods.cs`；M0 探针实测 5 个 DLL / 41 个入口全部可用，DWM 合成开启。
- 默认运行模式为**模式 C**（只显示悬浮 Dock，不接管原生任务栏），零风险；M2 已确认继续维持模式 C，`TaskbarInterop.Hide/Restore` 保持可调用但不由组合根触发，AC-01/10/11 用快照模拟验证。
- 看门狗为独立 `WinDock.Watchdog.exe` 进程：命名管道单向心跳由主程序 UI 线程派发（UI 卡死也能被发现），10s 失联即恢复原生任务栏并留恢复事件；不注册服务、不提权、不注入、不创建窗口。
- 零外部 NuGet 依赖（`src/NuGet.config` 清空 packageSources），离线可构建。

## 构建与运行

```powershell
# 构建 + 离线自检（日常构建完全离线）
powershell -ExecutionPolicy Bypass -File tools/build_m1.ps1 -Configuration Release -SelfTest

# 发布：自包含单文件（每个 ~62MB，无需 .NET 运行时）+ 框架依赖回退
# 运行时包需要联网还原，脚本只在发布步骤注入 nuget.org，仓库内 NuGet.config 仍清空包源
powershell -ExecutionPolicy Bypass -File tools/build_m1.ps1 -Publish

# 实机冒烟：启动 DockHost，30 秒空闲 CPU / 内存采样（生成 M1 记录请显式 --output）
python tools/smoke_m1.py --sample-seconds 30 --output docs/tmp-smoke.json

# 全功能验证：拖拽排序 + 悬停预览 + 看门狗强杀/正常退出 + 稳态采样
python tools/smoke_m1.py --restart --test-drag --test-preview --test-watchdog `
  --park-cursor --sample-seconds 12 --settle-seconds 6 `
  --output docs/M2-smoke-batch6-k17.json

# 纯稳态 30 秒基线（8.1 口径：光标停在中性区）
python tools/smoke_m1.py --restart --park-cursor --sample-seconds 30 `
  --settle-seconds 10 --cpu-trace --output docs/M2-smoke-batch6-idle.json

# 第八批：AC-05 关闭按钮 / AC-21 帧率 / AC-12 DPI 审计（需要交互式桌面，锁屏时全屏判定会隐藏 Dock）
python tools/smoke_m1.py --close-via-preview --sample-seconds 2 --settle-seconds 2 \
  --output docs/M2-smoke-batch7-close.json
python tools/smoke_m1.py --fps-probe --sample-seconds 2 --settle-seconds 2 \
  --output docs/M2-smoke-batch7-fps.json
python tools/smoke_m1.py --dpi-audit --sample-seconds 2 --settle-seconds 2 \
  --output docs/M2-smoke-batch7-dpi.json

# WD-035 长稳实证：24h 采样（60s/点，可随时中断续采）+ 汇总
# 默认会先做交互预热再取基线（K-21：冷启动后首次走通预览/拖拽会抬升一截内存，
# 不预热的话前 40 分钟的预热曲线会被误判成「漂移」）；加 --no-preheat 可跳过
python tools/stability_m1.py --hours 24 --interval 60 --output docs/M2-stability.jsonl
python tools/stability_m1.py --summary --output docs/M2-stability.jsonl
```

> 长稳采样的三个注意点：① 写出方式是**追加**，中途断了重跑同一条命令即续采（不会覆盖已有样本，`runs` 字段按宿主 pid 分段）；② 采集器会拉起 `DockHost`，**终止采集器会连带回收宿主与看门狗**（同一进程树），这一轮不会写恢复事件，但宿主运行时长会重新计；③ 采样期间任何鼠标交互都会让私有字节跳 5~30MB，样本会打上 `suspected_interaction` 标记，汇总时与空闲漂移分开统计（`spike_samples` / `quiet_samples`）。

产物：`artifacts/WinDock-win-x64/`（**自包含单文件**，推荐交付）含 `DockHost.exe`（主程序）、`WinDock.Settings.exe`（设置窗口）、`WinDock.Watchdog.exe`（守护进程），**三者必须同目录**；守护进程缺失时主程序会记 WARN 并降级为关闭。自包含发布失败（离线缺 runtime packs）时脚本会自动回退生成 `artifacts/WinDock-framework-dependent/`（需 .NET 10 桌面运行时）。

主程序支持：
- `DockHost.exe --selftest`：**75 项**零依赖自检（配置 / 动画 / 调度器 / DWM 几何 / 引导 / 拖拽顺序持久化 / 看门狗协议 / 打点聚合 / 帧率统计 / 预设主题 / 配置导入导出 / 多显示器解析 / 系统区 / 材质与兼容性回退 / 冲突检测 / 毛玻璃服务 / 全屏 shell 窗口排除）。退出码 `0` 全通过、`1` 有失败、`2` 断言通过但结果文件写盘失败（**必须在互斥之前执行**，否则 Dock 正在运行时会假通过）。
- `DockHost.exe --elevate`：R5「以管理员重启」引导进程——先请旧实例优雅退出，等单实例互斥释放后再 `runas` 拉起新实例。
- `DockHost.exe --exit`：请求已运行实例优雅退出（发看门狗 `S` 帧，不产生恢复事件）
- `DockHost.exe --dump-layout=<nonce>`：只读布局探针，把每个图标的屏幕坐标写入 `[Layout]` 日志（供自动化精确定位；nonce 原样回写，避免读到上一次的旧行）

托盘为纯 Win32 实现（`Shell_NotifyIcon` + 原生菜单，无 WinForms 依赖）；`behavior.metricsEnabled`（默认开）输出本地性能打点与 CPU 突发归因（`[Metrics]` 日志行，不上报）。

交付形态：`artifacts/WinDock-win-x64/` 为**自包含单文件**（无需安装运行时，K-02 已达成）；`artifacts/WinDock-framework-dependent/` 仅在自包含失败时作为回退产出，需 .NET 10 桌面运行时。仓库内 `src/NuGet.config` 仍清空包源，日常构建与自检完全不联网，仅自包含发布这一步还原 runtime packs。

只读环境探针与 GitHub 导入工具：

```powershell
python tools/probe_capabilities.py
python tools/create_github_project.py --dry-run
# 6.15 / 4.4 卸载还原：结束进程 → 还原任务栏位置与 HKCU 精简项 → 清开机自启 → 可选删文件
powershell -ExecutionPolicy Bypass -File tools/uninstall_windock.ps1 -InstallDir "artifacts/WinDock-win-x64"
```

## 状态

- [x] M0 技术预研、环境核查、GitHub 仓库与看板搭建
- [x] M1 MVP（WD-001~013；设计说明 §5 六条默认值已确认采纳）
- [x] M2 体验完善（WD-014~020、WD-033~039，八批实施；验收评审待走）
- [x] M3 正式版（WD-021~027 七项：多屏/图标清晰度/主题/毛玻璃/托盘设置/系统区/分辨率变化）
- [x] M4 增强（WD-028~032 五项：i18n / 拖入添加 / 配置导入导出 / 最小化反馈 / Jump List）
- [ ] 用户体验问题收口（毛玻璃实际观感、Dock 长度随运行应用变化、自动隐藏后唤出失效、图标锐度）
