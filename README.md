# WinDock

把 Windows 11 任务栏改造成 macOS Dock 风格的桌面应用：悬浮胶囊、邻域放大、运行指示点、窗口缩略图预览、首次启动引导、自动隐藏、拖拽排序、独立进程看门狗与多屏适配。

> **当前阶段**：M1 MVP（WD-001~013）已落地；**M2 六批已实施**（需求 14.1 已满足）：第一批预研 / 骨架 / WD-018 图标缓存，第二批 WD-019 动画 + WD-015 自动隐藏 / 热区 / 全屏 / 热键，第三批 WD-014 缩略图预览 + WD-017 首次引导，第四批 WD-016 拖拽排序 + WD-020 独立进程 Watchdog，第五批 WD-033 内置打点与 CPU 突发归因 + WD-034 托盘去 WinForms 化，第六批 K-17 交互复核 + WD-035 长稳采样器 + **K-02 自包含单文件发布达成**。Release 构建 0 警告 0 错误（**无 WinForms 依赖**）、离线自检 **38/38**、全功能冒烟 **PASS**（拖拽落盘 / 预览开合 / 看门狗强杀恢复 + 优雅握手）。
>
> **性能如实说明**：稳态 CPU 突发根因已定位为周期性唤醒的 15.6ms 记账刻度（枚举移后台 / 全屏退避 / 心跳间隔）。第六批为修 K-18 看门狗假阳性把心跳从 5s 收紧到 3.3s，30s 稳态 CPU **0.573%**（分段 0~0.938%，无大突发）；**对照实验关闭看门狗 = 0.104% ≤0.3% 达标**——8.1 与 AC-11 的 UI 心跳存在内在张力，口径待产品确认（K-13）。**注意口径**：需求 8.1 / AC-20 写的是「任务管理器 **5 分钟平均**」，长稳采样按 5 分钟滚动窗口算得 **0.17~0.19%（≤0.3%，达标率 1.0，初步待 24h 定稿）**；30s 单次采样的 0.573% 是 15.6ms 记账粒度放大，**不适合用来判 8.1**。内存 **199MB**（去 WinForms 后原 ~218MB），仍 >120MB（WPF D3D / Intel 驱动结构性开销，K-01/K-14）。**长稳实证进行中**（`tools/stability_m1.py` 24h 采样），数据齐全前不宣称「1 天不崩」。详见 [M2 变更说明与已知问题](docs/M2-变更说明与已知问题.md)；进度见 [M2 设计说明与任务拆解](docs/M2-设计说明与任务拆解.md) §4.1~4.6。

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
| [M2 变更说明与已知问题](docs/M2-变更说明与已知问题.md) | 阶段性交付物、第六批变更清单、验证结果、K-01~K-19 已知问题、长期结论与下一步 |
| [M2 指标与验收口径待确认清单](docs/M2-指标与验收口径待确认清单.md) | 待产品/评审拍板的 6 条口径（CPU 判定窗口、内存阈值、AC-05 / AC-12 / AC-21 缺口、AC-11 张力），每条含原文、实测、建议 |
| [M2-probe-raw.json](docs/M2-probe-raw.json) | M2 能力预研原始数据（DWM / QUNS / Flash / HotKey / 热区） |
| [M2-smoke-batch2.json](docs/M2-smoke-batch2.json) | 第二批实机冒烟：自动隐藏、热区恢复、稳态 CPU 分段 |
| [M2-smoke-batch3.json](docs/M2-smoke-batch3.json) | 第三批功能冒烟：引导窗口、预览面板、稳态 CPU；`-initial` 保留回环回归现场，`-idle-trace` 为 30s 分段复采 |
| [M2-smoke-batch4.json](docs/M2-smoke-batch4.json) | 第四批功能冒烟：拖拽顺序落盘、预览开合、看门狗强杀恢复与正常退出 S 帧握手 |
| [M2-smoke-batch4-idle.json](docs/M2-smoke-batch4-idle.json) | 第四批 30s 纯稳态 CPU 分段与内存 |
| [M2-smoke-batch5*.json](docs/M2-smoke-batch5-full.json) | 第五批：全功能冒烟 / 30s 稳态（0.417%）/ 关看门狗对照（0.104% 达标）/ 锁屏环境对照 |
| [M2-smoke-batch6-k17.json](docs/M2-smoke-batch6-k17.json) | 第六批：解锁后全功能冒烟（拖拽 / 预览 / 看门狗 PASS，K-17 关闭） |
| [M2-smoke-batch6-idle.json](docs/M2-smoke-batch6-idle.json) | 第六批 30s 纯稳态（3.3s 心跳，0.573% / 199MB） |
| [M2-stability.jsonl](docs/M2-stability.jsonl) | WD-035 长稳采样（60s/点，逐行 JSON；`--summary` 生成同名 `.summary.json`：5 分钟滚动均值 + 达标窗口占比 + 宿主 pid 分段 + GDI/USER 漂移） |
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

# WD-035 长稳实证：24h 采样（60s/点，可随时中断续采）+ 汇总
python tools/stability_m1.py --hours 24 --interval 60 --output docs/M2-stability.jsonl
python tools/stability_m1.py --summary --output docs/M2-stability.jsonl
```

> 长稳采样的两个注意点：① 写出方式是**追加**，中途断了重跑同一条命令即续采（不会覆盖已有样本，`runs` 字段按宿主 pid 分段）；② 采集器会拉起 `DockHost`，**终止采集器会连带回收宿主与看门狗**（同一进程树），这一轮不会写恢复事件，但宿主运行时长会重新计。

产物：`artifacts/WinDock-win-x64/`（**自包含单文件**，推荐交付）与 `artifacts/WinDock-framework-dependent/`（需 .NET 10 桌面运行时），两者都含 `DockHost.exe`（主程序）、`WinDock.Settings.exe`（设置窗口）、`WinDock.Watchdog.exe`（守护进程），**三者必须同目录**；守护进程缺失时主程序会记 WARN 并降级为关闭。

主程序支持：
- `DockHost.exe --selftest`：**38 项**零依赖自检（配置 / 动画 / 调度器 / DWM 几何 / 引导 / 拖拽顺序持久化 / 看门狗协议 / 打点聚合）
- `DockHost.exe --exit`：请求已运行实例优雅退出（发看门狗 `S` 帧，不产生恢复事件）
- `DockHost.exe --dump-layout=<nonce>`：只读布局探针，把每个图标的屏幕坐标写入 `[Layout]` 日志（供自动化精确定位；nonce 原样回写，避免读到上一次的旧行）

托盘为纯 Win32 实现（`Shell_NotifyIcon` + 原生菜单，无 WinForms 依赖）；`behavior.metricsEnabled`（默认开）输出本地性能打点与 CPU 突发归因（`[Metrics]` 日志行，不上报）。

交付形态二选一：`artifacts/WinDock-win-x64/` 为**自包含单文件**（无需安装运行时，K-02 已达成）；`artifacts/WinDock-framework-dependent/` 需 .NET 10 桌面运行时。仓库内 `src/NuGet.config` 仍清空包源，日常构建与自检完全不联网，仅自包含发布这一步还原 runtime packs。

只读环境探针与 GitHub 导入工具：

```powershell
python tools/probe_capabilities.py
python tools/create_github_project.py --dry-run
```

## 状态

- [x] M0 技术预研、环境核查、GitHub 仓库与看板搭建
- [x] M1 MVP（WD-001~013；设计说明 §5 六条默认值已确认采纳）
- [ ] M2 体验完善 / M3 正式版 / M4 增强
