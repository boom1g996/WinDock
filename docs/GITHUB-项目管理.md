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
| Labels | 脚本创建 17 个（P0/P1/P2、9 个 module、4 个 type、risk:system-api）；加上 GitHub 仓库默认 10 个，合计 27 个 |
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

API 方式通过 Git Data API 逐个重放本地提交（blob → tree → commit → ref），保留提交顺序与 message。**空仓库**会先用 Contents API 建一个引导 commit 激活仓库，再用本地历史替换该 ref；后续调用按 commit message 定位远端位置，只推送新增提交；历史分叉时拒绝强制覆盖。

## 5. 已知约束

1. **网络**：本机代理对 `github.com` 的 git smart-HTTP 返回 `502`，直连 443 超时；REST / GraphQL 的 `api.github.com` 通道正常。这是环境限制，不是仓库权限问题。
2. **GitHub MCP 连接器**：当前授权为只读 GitHub App，`create_repository` 返回 `403 Resource not accessible by integration`；写操作一律走 PAT 脚本。
3. **PAT 安全**：脚本不打印、不落盘 token；`git push` 用 `GIT_CONFIG_*` 环境变量注入认证头，不写 `.git/config`。曾明文出现过的 PAT 应撤销并轮换。
4. **提交 SHA 不一致**：API 重放会按服务端时间重新生成 commit 对象，远端与本地的 commit SHA 不同（内容、顺序、message 一致）。因此将来即使网络恢复，也不要直接 `git push`（会被判为分叉）；继续用 `--push-api` 增量同步，或在可直连的机器上重新 clone 后以远端为准。
5. **凭据状态（2026-11-09 更新）**：此前在会话中明文出现的 classic PAT 已失效，`GET /repos/boom1g996/WinDock` 返回 **401 Bad credentials**（GitHub 会对公开泄露的 token 自动撤销，也可能已被手动轮换）。**M1 代码提交 `53c0868` 目前只在本地**，远端 `main` 仍停在上一轮同步的历史。恢复步骤：新建 classic PAT（`repo` + `project`）→ 设为 `GITHUB_TOKEN` 环境变量（不要在聊天里贴）→ `python tools/create_github_project.py --push-api`。**不要重复尝试旧 token**；401 是确定性失败，不是限流或临时故障。
6. **M1 状态**：设计说明 §5 六条默认值已获用户「按默认」确认，WD-001~013 代码已落地；交付清单与 10 项已知问题见 `docs/M1-变更说明与已知问题.md`。看板 M1 卡片状态暂未改为 Done：M1 出口标准「可日常使用 1 天不崩」尚未实跑实证（K-08），不宜提前标完成。
