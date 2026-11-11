# WinDock Backlog · GitHub 项目看板导入清单

- 目标看板：`https://github.com/users/boom1g996/projects/1`
- 生成日期：2026-11-09　|　数据源：`需求文档.md` v1.0 第 11 章 + `M1-设计说明与任务拆解.md` + `M2-设计说明与任务拆解.md`
- 状态：**39 议题 / 39 看板卡片已导入**（WD-033~036 为 M2 新增，对应 Issue #36~#39，Status `Todo` / Priority `P0`）；看板统计 Done 3 / Todo 36。
- 2026-11-11 追加 **M5 里程碑（产品转向「任务栏替代」）**：WD-040~047 为本批新增，本地已完成 M5-1（WD-040~044 代码 + WD-045 设置 + WD-046 交付），待随下次同步导入看板。
- 编号规则：`WD-xxx` 为全局唯一任务号；M0 已完成项以 `done` 标记，导入后直接关闭。

---

## 1. Milestones

| 名称 | 范围 | 出口标准 |
| --- | --- | --- |
| M0 技术预研 | 验证第 4.3 表全部能力，产出可行性报告 | 每项标注可行/降级/不可行 |
| M1 MVP | Dock 渲染、图标、启动/切换、放大、右键菜单、基础设置、托盘、恢复原生 | 可日常使用 1 天不崩 |
| M2 体验完善 | 缩略图预览、自动隐藏、全屏检测、拖拽排序、首次引导、图标缓存、崩溃守护 | 通过第 12 章功能验收 |
| M3 正式版 | 多显示器、位置切换、主题预设、系统区按钮、共存模式、i18n | 通过性能与兼容性矩阵 |
| M4 增强 | 更新器、Jump List、文件夹固定、导入导出、Dock 侧最小化反馈 | — |
| M5 任务栏替代 | 三段式全宽任务栏、启动台、托盘镜像、控制中心、桌面组件 | 可日常替代 Windows 任务栏（`toolbar` 形态下 1 天不崩、AC-20/21 达标） |

## 2. Labels

`P0` `P1` `P2` · `module:taskbar` `module:window` `module:icon` `module:renderer` `module:animation` `module:preview` `module:config` `module:settings` `module:watchdog` · `type:research` `type:feature` `type:chore` `type:test` · `risk:system-api`

## 3. Issues（表格即导入源，脚本按 `|` 解析）

| ID | 标题 | Milestone | 优先级 | Labels | 依赖 | 验收 / 出口标准 | 状态 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| WD-M0-01 | 编写只读系统能力探针脚本 | M0 技术预研 | P0 | type:research,type:chore | — | 探针不改动任何窗口，输出结构化 JSON | done |
| WD-M0-02 | 实测第 4.3 表全部系统能力并输出可行性报告 | M0 技术预研 | P0 | type:research | WD-M0-01 | 15 项均标注可行/降级/不可行 | done |
| WD-M0-03 | 核查 GitHub 看板与构建链环境 | M0 技术预研 | P0 | type:research | — | 明确 B1~B4 阻塞项 | done |
| WD-001 | 搭建解决方案骨架与构建脚本 | M1 MVP | P0 | type:chore | B1 | dotnet build 零警告，产出自包含 exe | todo |
| WD-002 | ConfigService 配置读写与容错 | M1 MVP | P0 | module:config,type:feature | WD-001 | AC-09 / AC-32 | todo |
| WD-003 | TaskbarInterop 任务栏隐藏与恢复 | M1 MVP | P0 | module:taskbar,risk:system-api,type:feature | WD-001 | AC-01 / AC-10 / AC-30；按实测顶部边缘动态处理 | todo |
| WD-004 | WindowEnumerator 事件驱动窗口枚举 | M1 MVP | P0 | module:window,type:feature | WD-001 | 419→7 类过滤结果一致，无轮询 | todo |
| WD-005 | IconService 高清图标提取 | M1 MVP | P0 | module:icon,type:feature | WD-001 | 256px 图标 200% DPI 清晰 | todo |
| WD-006 | DockRenderer 悬浮胶囊与图标区 | M1 MVP | P0 | module:renderer,type:feature | WD-002,WD-004,WD-005 | FR-101~104 | todo |
| WD-007 | AnimationEngine 邻域联动放大 | M1 MVP | P0 | module:animation,type:feature | WD-006 | AC-02 / AC-21，空闲不订阅 Rendering | todo |
| WD-008 | 点击启动与窗口切换语义 | M1 MVP | P0 | module:renderer,module:window,type:feature | WD-004,WD-006 | AC-04，10 次 ≥9 次；P95 ≤150ms | todo |
| WD-009 | 图标右键菜单 | M1 MVP | P0 | module:settings,type:feature | WD-004,WD-006 | FR-501 结构完整 | todo |
| WD-010 | 设置窗口基础项与实时预览 | M1 MVP | P0 | module:settings,type:feature | WD-002,WD-006,WD-007 | AC-09，200ms 内生效 | todo |
| WD-011 | 托盘图标与菜单 | M1 MVP | P0 | module:settings,type:feature | WD-003 | AC-10，退出 3s 内恢复 | todo |
| WD-012 | 异常兜底与崩溃时恢复任务栏 | M1 MVP | P0 | module:watchdog,risk:system-api,type:feature | WD-003 | 8.2 未处理异常全捕获，无系统崩溃框 | todo |
| WD-013 | M1 集成自测与性能基线 | M1 MVP | P0 | type:test | WD-001~012 | AC-20/21/22，产出变更说明与已知问题 | todo |
| WD-014 | DWM 缩略图预览面板 | M2 体验完善 | P0 | module:preview,type:feature | WD-004 | AC-05；最多 6 个、关闭即注销 | todo |
| WD-015 | 自动隐藏、边缘热区与全屏检测 | M2 体验完善 | P0 | module:taskbar,module:renderer,type:feature | WD-006 | AC-07 / AC-08；热区按实测边缘动态生成 | todo |
| WD-016 | 拖拽排序、固定与拖出移除 | M2 体验完善 | P0 | module:renderer,type:feature | WD-006 | AC-06 | todo |
| WD-017 | 首次启动引导 | M2 体验完善 | P0 | module:settings,type:feature | WD-003,WD-010 | 3 步：权限说明→共存模式→预设主题 | todo |
| WD-018 | 图标磁盘缓存与失效重建 | M2 体验完善 | P0 | module:icon,type:feature | WD-005 | 冷启动 ≤300ms，mtime 变化自动失效 | todo |
| WD-019 | 启动跳动、注意跳动与指示点动画 | M2 体验完善 | P0 | module:animation,type:feature | WD-007 | AC-03 / FR-202~204 | todo |
| WD-020 | Watchdog 守护进程与心跳恢复 | M2 体验完善 | P0 | module:watchdog,risk:system-api,type:feature | WD-012 | AC-11，10s 未心跳恢复任务栏 | todo |
| WD-021 | 多显示器独立 Dock 与 DPI 适配 | M3 正式版 | P1 | module:renderer,risk:system-api,type:feature | WD-006 | AC-12 / AC-31；本机双屏负坐标必测 | todo |
| WD-022 | 左/右位置与垂直朝向 | M3 正式版 | P1 | module:renderer,type:feature | WD-006 | FR-6.8；不支持顶部 | todo |
| WD-023 | 预设主题与全量外观参数 | M3 正式版 | P1 | module:settings,type:feature | WD-010 | 5 套预设，可复制微调 | todo |
| WD-024 | 系统区按钮（回收站/任务视图/显示桌面） | M3 正式版 | P1 | module:renderer,type:feature | WD-006 | FR-6.12 | todo |
| WD-025 | 共存模式 B 与模式切换 | M3 正式版 | P1 | module:taskbar,risk:system-api,type:feature | WD-003 | 3s 内切换完成 | todo |
| WD-026 | 中英文国际化与可访问性 | M3 正式版 | P1 | type:feature | WD-010 | 8.4 / 8.5；UIA 属性、键盘可达、减少动态效果 | todo |
| WD-027 | 性能与兼容性矩阵回归 | M3 正式版 | P1 | type:test | WD-013 | 8.1 / 8.3 全表，含 100/150/200% DPI | todo |
| WD-028 | 检查更新与安装包卸载还原 | M4 增强 | P2 | type:feature | WD-003 | 6.15；卸载 100% 还原任务栏 | todo |
| WD-029 | Jump List 任务项 | M4 增强 | P2 | module:settings,type:feature | WD-009 | ICustomDestinationList | todo |
| WD-030 | 文件夹固定与拖入添加 | M4 增强 | P2 | module:icon,type:feature | WD-016 | FR-303 | todo |
| WD-031 | 配置导入导出 | M4 增强 | P2 | module:config,type:feature | WD-002 | 6.14 | todo |
| WD-032 | Dock 侧最小化反馈动效 | M4 增强 | P2 | module:animation,type:feature | WD-019 | 按 6.9 降级方案，不劫持系统动画 | todo |
| WD-033 | 内置打点与第 8.1 表全量回归 | M2 体验完善 | P0 | type:test | WD-014~020 | AC-20/21/22；需求 14.5 超标必须修复后提交 | todo |
| WD-034 | 托盘去 WinForms 化与内存复核 | M2 体验完善 | P0 | module:settings,type:feature | WD-011 | AC-20 内存 ≤120MB；禁用硬件加速凑数不允许 | todo |
| WD-035 | 长稳实证（1 天 + 72h） | M2 体验完善 | P0 | type:test | WD-033 | 8.2；24h 内存增长 ≤5MB；补齐 M1 遗留 K-08 | todo |
| WD-036 | M2 交付物（安装包 + 变更说明 + 已知问题） | M2 体验完善 | P0 | type:chore | WD-014~035 | 需求第 11 章里程碑交付约束 | todo |
| WD-040 | 任务栏双布局骨架（dock / toolbar）与三段结构 | M5 任务栏替代 | P0 | module:renderer,type:feature | WD-006 | 切换模式后布局正确；旧 Dock 行为不回归 | done |
| WD-041 | 任务栏尺寸与定位引擎（fill / fit） | M5 任务栏替代 | P0 | module:renderer,type:feature | WD-040 | 两种宽度策略矩形正确，不出现 k24 窄条 | done |
| WD-042 | 左段能力：启动台入口 / 显示器切换 / 当前窗口实况缩略图 | M5 任务栏替代 | P0 | module:renderer,module:window,type:feature | WD-041 | 前台切换 300ms 内更新；无窗口显示占位 | done |
| WD-043 | 时钟组件（对齐整分定时器 + 格式容错） | M5 任务栏替代 | P0 | module:renderer,type:feature | WD-041 | 空闲 CPU 增量 <0.02%；非法格式串不抛异常 | done |
| WD-044 | 启动台应用网格面板（搜索 + 分批填充） | M5 任务栏替代 | P0 | module:renderer,type:feature | WD-042 | 打开 <200ms；快速关开不串数据 | done |
| WD-045 | 设置面板「任务栏」分组与原生任务栏接管 | M5 任务栏替代 | P0 | module:settings,type:feature | WD-002,WD-003 | 9 项可热更；接管与 general.taskbarMode 双向同步 | done |
| WD-046 | M5-1 交付（构建 / 自检 / 变更说明与已知问题） | M5 任务栏替代 | P0 | type:test,type:chore | WD-040~045 | 0 警告 0 错误、自检全绿、可运行自包含包 | done |
| WD-047 | 系统托盘镜像 UIA 可行性探测 | M5 任务栏替代 | P0 | type:research,risk:system-api | WD-042 | 能从 TrayNotifyWnd 稳定枚举 ≥1 个图标，否则降级 | todo |
| WD-048 | 系统托盘镜像实现（图标 + 点击委托） | M5 任务栏替代 | P1 | module:renderer,risk:system-api,type:feature | WD-047 | 枚举开销 <300ms；不阻塞 UI 线程 | todo |
| WD-049 | 日历 + 通知面板（中段时钟点击） | M5 任务栏替代 | P1 | module:renderer,type:feature | WD-043 | 点击时钟浮出面板，Esc/失焦关闭 | todo |
| WD-050 | 启动台支持 UWP 应用（AppsFolder 枚举） | M5 任务栏替代 | P1 | module:icon,type:feature | WD-044 | 设置/计算器/照片等 UWP 项可见可启动 | todo |
| WD-051 | 控制中心（WiFi / 蓝牙 / 音量 / 亮度） | M5 任务栏替代 | P1 | module:renderer,risk:system-api,type:feature | WD-042 | 四项可读可写，失败有降级提示 | todo |
| WD-052 | 设置中心改左侧导航（7 个分区） | M5 任务栏替代 | P1 | module:settings,type:feature | WD-045 | 分区切换 <100ms，配置项无丢失 | todo |
| WD-053 | 桌面组件（天气 / 媒体播放 / 性能） | M5 任务栏替代 | P2 | module:renderer,type:feature | WD-041 | 组件可开关、不拖慢空闲 CPU | todo |
| WD-054 | 玻璃雨装饰特效 | M5 任务栏替代 | P2 | module:animation,type:feature | WD-041 | 默认关闭；开启后仍 ≥55 FPS | todo |

## 4. 导入约定

1. Issue 标题格式：`WD-xxx 标题`（不带额外前缀，符合团队命名习惯）。
2. Issue body 由脚本按「依赖 / 验收 / 来源」三段生成，并回链到 `docs/M0-技术预研报告.md`、`docs/M1-设计说明与任务拆解.md` 与 `docs/M2-设计说明与任务拆解.md`。
3. `done` 状态的 M0 项创建后立即 close。
4. 看板列建议：`Backlog` → `Todo` → `In Progress` → `In Review` → `Done`；`risk:system-api` 标签单独建视图，便于回归时重点盯。
