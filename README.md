# WinDock

把 Windows 11 任务栏改造成 macOS Dock 风格的桌面应用：悬浮胶囊、邻域放大、运行指示点、窗口预览、自动隐藏与多屏适配。

> **当前阶段**：M1 MVP 已落地（WD-001~013）并同步远端。构建 0 警告 0 错误、离线自检 7/7、实机冒烟 PASS；30 秒空闲 CPU **0.208%**（需求 ≤0.3%）。**M2 设计说明与任务拆解已产出，状态「待确认」——按需求第 14 章，确认后才写产品代码。** M1 交付清单、性能实测与已知问题见 [docs/M1-变更说明与已知问题.md](docs/M1-变更说明与已知问题.md)。

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
| [M2 设计说明与任务拆解](docs/M2-设计说明与任务拆解.md) | 10 条设计决策、新增接口边界、WD-014~036 任务与验收映射、6 条待确认默认值（**待确认**） |
| [M1-smoke-result.json](docs/M1-smoke-result.json) | 实机冒烟原始结果（窗口 / CPU / 内存 / 日志错误） |
| [BACKLOG](docs/BACKLOG.md) | 5 里程碑 / 39 议题（含 M2 新增 4 条）/ 17 标签 |
| [M0-probe-raw.json](docs/M0-probe-raw.json) | 只读系统探针原始输出 |

需求唯一真源：桌面 `Win11Dock/需求文档.md` v1.0（未纳入本仓库）。

## 技术栈

- **.NET 10 LTS + WPF**。需求文档 4.2 原推荐 .NET 8，但 .NET 8 已于 2026-11-10 终止官方支持，故改用支持至 2028-11-14 的 .NET 10；WPF 用法完全一致。SDK 10.0.403 已验证可构建（0 警告 0 错误）。
- 实际 TFM 为 `net10.0-windows`（非设计说明中的 `net10.0-windows10.0.22000.0`）：离线 NuGet 源清空后带 Windows SDK 版本号的 TFM 需要还原 `Microsoft.Windows.SDK.NET.Ref`，故降级为纯 Win32 P/Invoke 方案。
- 系统互操作靠 P/Invoke，全部声明集中在 `WinDock.Core/Native/NativeMethods.cs`；M0 探针实测 5 个 DLL / 41 个入口全部可用，DWM 合成开启。
- 默认运行模式为**模式 C**（只显示悬浮 Dock，不接管原生任务栏），零风险；隐藏任务栏等写入能力留到 M2 且需单独验证。
- 零外部 NuGet 依赖（`src/NuGet.config` 清空 packageSources），离线可构建。

## 构建与运行

```powershell
# 构建 + 离线自检 + 发布（自动处理 NU1100 回退）
powershell -ExecutionPolicy Bypass -File tools/build_m1.ps1 -Configuration Release -Publish -SelfTest

# 实机冒烟：启动 DockHost，30 秒空闲 CPU / 内存采样
python tools/smoke_m1.py --sample-seconds 30
```

产物目录 `artifacts/WinDock-framework-dependent/` 下 `DockHost.exe`（主程序）与 `WinDock.Settings.exe`（设置窗口）**必须同目录**。主程序另支持 `DockHost.exe --selftest` 运行 7 项零依赖自检（配置夹紧、损坏回退、原子写、未知字段、中英资源）。

当前为 framework-dependent 发布，运行需 .NET 10 桌面运行时；离线环境缺 runtime packs 导致自包含单文件发布回退，见已知问题 K-02。

只读环境探针与 GitHub 导入工具：

```powershell
python tools/probe_capabilities.py
python tools/create_github_project.py --dry-run
```

## 状态

- [x] M0 技术预研、环境核查、GitHub 仓库与看板搭建
- [x] M1 MVP（WD-001~013；设计说明 §5 六条默认值已确认采纳）
- [ ] M2 体验完善 / M3 正式版 / M4 增强
