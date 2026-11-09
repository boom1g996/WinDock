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

### 4.0 凭据设置（前置，只做一次）

**生成 PAT**：GitHub → Settings → Developer settings → Personal access tokens → **Tokens (classic)** → Generate new token (classic)。Note 填 `WinDock-sync`，Expiration 建议 7~30 天，勾选 **`repo`** + **`project`**（本脚本不需要 `workflow`），生成后立刻复制（只显示一次）。

**设为用户级持久环境变量**：本工作区每条命令都由独立新进程执行，会话级 `$env:GITHUB_TOKEN = ...` 在下一个进程会丢失，因此必须写用户级变量。在**你自己电脑的 PowerShell** 执行（`-AsSecureString` 保证输入不回显、不进命令历史）：

```powershell
$sec = Read-Host -AsSecureString "粘贴新 PAT"
[Environment]::SetEnvironmentVariable(
  "GITHUB_TOKEN",
  [System.Net.NetworkCredential]::new("", $sec).PlainPassword,
  "User")
```

助手会从注册表读取该值并注入同步进程，token 不打印、不写入仓库、不进 `.git/config`。若不想写注册表，也可把单行 token 存到**仓库外**的文件（如 `C:\Users\83892\.windock-github-token`）并告知路径，用完即删。

> **不要在聊天里贴 token**：GitHub 会对公开泄露的 token 自动 revoke（旧 PAT 的 `401 Bad credentials` 即由此引发）。若采用文件方式，用完请立即删除该文件并按需轮换 PAT。

### 4.1 新增或调整任务

1. 编辑 `docs/BACKLOG.md` 的 Issues 表格（列顺序固定：ID / 标题 / Milestone / 优先级 / Labels / 依赖 / 验收 / 状态）。
2. 本地提交：`git add -A && git commit -m "chore: 更新任务拆解"`。
3. 同步到 GitHub：

```powershell
# 凭据按 §4.0 预先设为用户级环境变量；如在当前终端直接跑，先注入一次：
$env:GITHUB_TOKEN = [Environment]::GetEnvironmentVariable("GITHUB_TOKEN", "User")
python tools/create_github_project.py --project-number 1
```

脚本会：跳过同名议题 → 新建增量议题 → 把全部 35+ 条重新挂看板（已存在的跳过）→ 补齐 Status / Priority。若由助手代跑，token 由助手从注册表读出后注入，无需出现在命令里。

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
5. **凭据状态（2026-11-09 14:15 已恢复）**：此前明文出现的 classic PAT 失效导致的 `401 Bad credentials` 已解除。用户提供新 classic PAT（身份 `boom1g996`，scope 含 `repo` + `project`），以**仓库外单行文件**方式交给助手读取，仅在同步进程内注入 `GITHUB_TOKEN`。`--push-api` 增量推送 3 个提交成功：`53c0868` → `2b3693a7`、`6af0419` → `448793c4`、`9c297e7` → `94f600e7`；远端 `main` HEAD 为 `94f600e7`，复核 49 个文件齐全（含 `src/`、`docs/M1-*`），`artifacts/` 按预期排除。**旧 token 不要再试**；用完请删除本地 token 文件并按需轮换。
6. **M1 状态**：设计说明 §5 六条默认值已获用户「按默认」确认，WD-001~013 代码已落地并同步至远端；交付清单与 10 项已知问题见 `docs/M1-变更说明与已知问题.md`。看板 M1 卡片状态暂未改为 Done：M1 出口标准「可日常使用 1 天不崩」尚未实跑实证（K-08），不宜提前标完成。
7. **M2 状态**：§6 六条默认值已由用户「按默认，开始」确认，产品代码已开工。第一批（能力预研 / `NativeMethods.M2` / 9 个 M2 接口 / `TimerScheduler` / 配置扩展 / WD-018 图标磁盘缓存）与第二批（WD-019 五通道动画扩展、WD-015 自动隐藏 + 2px 热区 + 全屏检测 + `Ctrl+Alt+D`）均已落地；构建 0 警告 0 错误、离线自检 **12/12**、实机冒烟 PASS（`docs/M2-smoke-batch1.json` / `M2-smoke-batch2.json`）。WD-033~036 已导入为 Issue #36~#39 与看板卡片（Todo / P0），看板 39 卡片（Done 3 / Todo 36）与 Backlog 一致。第三批（WD-014 DWM 缩略图预览 / WD-017 首次启动引导）已落地：构建 0 警告 0 错误、离线自检 **21/21**、功能冒烟 PASS（`docs/M2-smoke-batch3.json`）；空闲 CPU / 内存仍未达标（K-13 / K-14，见 `docs/M2-变更说明与已知问题.md`），不提前标 Done。剩余 WD-016 / 020 / 033~036 按设计说明 §4.3 后续批次实施；M2 卡片仍不标 Done（K-08 长稳未实证）。**第三批代码同步远端待用户提供仓库外单行 PAT 文件**（桌面 `pat.txt` 已不存在、用户级 `GITHUB_TOKEN` 未配置），同步方式仍为 `--push-api`。
