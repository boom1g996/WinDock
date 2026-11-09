# WinDock · GitHub 项目管理手册

> 更新：2026-11-09 · 适用仓库 `boom1g996/WinDock` · 维护人：乔旭

## 1. 资源入口

| 资源 | 地址 |
| --- | --- |
| 代码仓库 | https://github.com/boom1g996/WinDock |
| Issues | https://github.com/boom1g996/WinDock/issues |
| 里程碑 | https://github.com/boom1g996/WinDock/milestones |
| 项目看板（Projects v2 #1「W&dock」） | https://github.com/users/boom1g996/projects/1/views/1 |
| 本地仓库 | `D:\workspace\W&dock` |
| 需求唯一真源 | `C:\Users\83892\Desktop\Win11Dock\需求文档.md` v1.0 |

## 2. 导入结果快照（2026-11-09）

| 项 | 数量 / 状态 |
| --- | --- |
| 仓库 | 1 个，public |
| Labels | 17 个（P0/P1/P2、9 个 module、4 个 type、risk:system-api） |
| Milestones | 5 个：M0 技术预研 / M1 MVP / M2 体验完善 / M3 正式版 / M4 增强 |
| Issues | 35 条（#1~#35）；M0 三条已关闭，其余 open |
| 看板卡片 | 35 张；Status `Done`×3 / `Todo`×32；Priority P0×23 / P1×7 / P2×5 |
| 代码 | 本地提交经 Git Data API 重放至 `main`，保留提交历史 |

议题标题以 `WD-xxx` 开头，是导入脚本的**幂等唯一键**；重跑不会重复建 issue。

## 3. 文档索引

| 文件 | 用途 |
| --- | --- |
| `docs/M0-技术预研报告.md` | 系统能力实测、可行性判定、阻塞项、SDK 验证 |
| `docs/M1-设计说明与任务拆解.md` | 设计说明、13 条 M1 任务、验收映射、6 条默认建议 |
| `docs/BACKLOG.md` | **任务导入源**（脚本直接解析其中的表格） |
| `docs/M0-probe-raw.json` | 只读探针原始输出 |
| `tools/probe_capabilities.py` | 可复跑的只读系统能力探针 |
| `tools/create_github_project.py` | GitHub 导入 / 同步脚本 |

## 4. 日常工作流

### 4.1 新增或调整任务

1. 编辑 `docs/BACKLOG.md` 的 Issues 表格（列顺序固定：ID / 标题 / Milestone / 优先级 / Labels / 依赖 / 验收 / 状态）。
2. 本地提交：`git add -A && git commit -m "chore: 更新任务拆解"`。
3. 同步到 GitHub：

```powershell
$env:GITHUB_TOKEN = "<classic PAT, 需 repo + project>"
python tools/create_github_project.py --project-number 1
```

脚本会：跳过同名议题 → 新建增量议题 → 把全部 35+ 条重新挂看板（已存在的跳过）→ 补齐 Status / Priority。**不要在聊天里贴 token。**

### 4.2 推进状态

- 日常直接在看板拖动 Status 列，或关闭对应 Issue；看板 `Closed` / `Milestone` 字段会自动同步。
- `docs/BACKLOG.md` 的「状态」列只在**重新全量导入**时起作用，日常以看板为准。

### 4.3 代码同步

| 方式 | 命令 | 适用 |
| --- | --- | --- |
| 普通 git | `git push origin main` | 能直连 github.com 的机器 |
| 脚本自动 | `python tools/create_github_project.py --push-local` | 先试 git push，失败自动回退 API |
| 仅 API | `python tools/create_github_project.py --push-api` | **本机推荐**，代理只放行 api.github.com |

API 方式通过 Git Data API 逐个重放本地提交（blob → tree → commit → ref），保留提交顺序与 message；远端 `main` 已存在且与本地 HEAD 不同时会拒绝覆盖，避免误删历史。

## 5. 已知约束

1. **网络**：本机代理对 `github.com` 的 git smart-HTTP 返回 `502`，直连 443 超时；REST / GraphQL 的 `api.github.com` 通道正常。这是环境限制，不是仓库权限问题。
2. **GitHub MCP 连接器**：当前授权为只读 GitHub App，`create_repository` 返回 `403 Resource not accessible by integration`；写操作一律走 PAT 脚本。
3. **PAT 安全**：脚本不打印、不落盘 token；`git push` 用 `GIT_CONFIG_*` 环境变量注入认证头，不写 `.git/config`。曾明文出现过的 PAT 应撤销并轮换。
4. **未开工项**：M1 代码尚未编写，等待 `docs/M1-设计说明与任务拆解.md` §5 的 6 条默认值批复（按默认即可开工 WD-001~013）。
