# WinDock

把 Windows 11 任务栏改造成 macOS Dock 风格的桌面应用：悬浮胶囊、邻域放大、运行指示点、窗口预览、自动隐藏与多屏适配。

> **当前阶段**：M0 技术预研已完成；M1 设计说明待批复，**产品代码尚未开工**。

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
| [M1 设计说明与任务拆解](docs/M1-设计说明与任务拆解.md) | 架构设计、13 条 M1 任务、验收映射、6 条默认建议 |
| [BACKLOG](docs/BACKLOG.md) | 5 里程碑 / 35 议题 / 17 标签 |
| [M0-probe-raw.json](docs/M0-probe-raw.json) | 只读系统探针原始输出 |

需求唯一真源：桌面 `Win11Dock/需求文档.md` v1.0（未纳入本仓库）。

## 技术栈

- **.NET 10 LTS + WPF**（`net10.0-windows`）。需求文档 4.2 原推荐 .NET 8，但 .NET 8 已于 2026-11-10 终止官方支持，故改用支持至 2028-11-14 的 .NET 10；WPF 用法完全一致。SDK 10.0.403 已验证可构建（0 警告 0 错误）。
- 系统互操作靠 P/Invoke：M0 探针实测 5 个 DLL / 41 个入口全部可用，DWM 合成开启。
- 默认运行模式为**模式 C**（只显示悬浮 Dock，不接管原生任务栏），零风险；隐藏任务栏等写入能力留到 M2 且需单独验证。

## 构建

M1 代码落地后补充。当前仓库仅含文档、只读探针与 GitHub 导入工具：

```powershell
python tools/probe_capabilities.py          # 只读系统能力探针
python tools/create_github_project.py --dry-run
```

## 状态

- [x] M0 技术预研、环境核查、GitHub 仓库与看板搭建
- [ ] M1 MVP（WD-001~013，等待设计说明 §5 批复）
- [ ] M2 体验完善 / M3 正式版 / M4 增强
