# Agent Timeline — PRD

> 常驻桌面的半透明时间线挂件：实时跟踪本机 AI agent 的 session 文件，把"我提交过的每条命令"提炼成时间线节点（关键点 / 需求点 / 任务代号），解决长周期任务中"代号忘了是啥、翻 session 找不到"的痛点。

## 1. 背景与核心诉求

长周期使用 agent CLI（Claude Code / Codex / Kimi / zcode）时：

- 一个任务跑几小时甚至几天，期间提交过的命令里出现的**任务代号 / 需求代号**（如 `T-PLUGIN-00`）会被遗忘其原始含义；
- 回翻 session 记录（jsonl 数万行）很难定位当初定义这些代号的那条命令；
- 用户只关心**自己提交的命令**及其拆解出的关键点 / 需求点 / 任务点，不关心 agent 的中间过程。

## 2. 已确认的需求决策（2026-07-25 与用户确认）

| 决策点 | 结论 |
|---|---|
| MVP agent 范围 | **Claude Code + Codex + Kimi + zcode**（zcode 本机未安装，做成预留适配器，待用户提供 session 样例点亮） |
| 摘要引擎 | **默认复用本机已装 CLI**（`claude -p` headless / `codex exec`），零配置；设置界面保留自定义 OpenAI-compatible provider（base URL + key + model）；LLM 不可用时降级为规则截取 |
| 节点粒度 | **命令 + 关键点 + 代号词典**：每条命令经 LLM 提炼标题与关键点/需求点/任务点；任务代号自动登记进词典，可点击回溯原始定义 |
| Windows 技术栈 | **WinUI 3**（Windows App SDK，Mica/Acrylic），托盘用 H.NotifyIcon |
| mac 技术栈 | Swift + SwiftUI，AppKit 补窗口特性（NSPanel / window level / NSVisualEffectView） |
| 开发顺序 | mac 先行完成并可运行；Windows 端交付可编译源码工程，由用户在 win 机器上调试 |

## 3. 功能需求

### 3.1 Session 跟踪（F1）

- 监听以下目录（FSEvents / ReadDirectoryChangesW），增量 tail 解析（记录每文件字节偏移，不重读全文件）：
  - Claude Code：`~/.claude/projects/<project-slug>/<session-uuid>.jsonl`
  - Codex：`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`
  - Kimi：`~/.kimi/sessions/<project-hash>/<session-uuid>/wire.jsonl`
  - zcode：路径待定，设置中可配置（预留适配器）
- 首次启动回填最近 7 天（可配置）的 session；
- 按项目 / session / agent 归组。

### 3.2 Timeline 展示（F2）

- 竖向时间线，**最新在最上**；
- 每条用户命令 = 一个节点，含：时间、项目名、agent 徽标、LLM 提炼的标题、关键点列表；
- 点击节点展开：原始 prompt 全文 + agent 执行结果一句话总结；
- 节点内出现的代号渲染为 chip，点击 / hover 显示原始定义与出处，可跳转到定义它的节点；
- 支持按项目 / agent 过滤（MVP 至少支持全部/单项目切换）。

### 3.2b 台账视觉语法（F2b，2026-07-26 新增）

节点采用无框"双墨线台账"条目（设计评审团合成方案），一条规则学一次：

- **❯ + 实线 agent 色墨线 + 高不透明纸面块 = 我的原话**——命令原文永远默认可见（折叠 3 行），纸面块比任何表面更不透明，保证失焦半透明态下唯一清晰的就是用户指令；左上角压平圆角指向 rail 标记；
- **✦ + 虚线灰墨线 = 机器提炼**——LLM 标题（与短命令重复时自动隐去）、关键点摘要行（折叠单行 +n 计数）、代号 chips、绿色结果行，整体缩进居次；
- **rail 语法**：连续时间轴线；需求/决策 = kind 色菱形锚点，任务/修复/调研/学习 = kind 色圆点，其他 = 空心小圆；首次定义代号的节点加 accent 色环（chip"跳转定义"的视觉落点）；需求/决策条目附 8% kind 色整条洗染；
- **日期分组**：今天 · n条 / 昨天 / MM-dd · 周X 置顶粘性分隔，带轨道横刻度；
- **交互**：整条点击展开（文本划选优先）、hover 浮现原话复制按钮（✓ 回执动效）、右键菜单（复制原话/复制摘要/跳转定义/只看此项目）、chips 命中区外扩；动效仅 opacity/位移/旋转（双端 1:1），尊重"减弱动态效果"。

### 3.3 代号词典（F3）——含生命周期（2026-07-26 扩展）

- **两类代号，两种识别策略**：
  1. 连字符长代号（`T-PLUGIN-00`、`REQ-AUTH-3`）：正则直接识别（原有能力）；
  2. **批量短代号（`N1`/`T2`/`M1`/`Q3` 等 `[A-Z]{1,4}\d{1,3}`）**：天然歧义大，只走两条安全通道进词典——
     a) **定义式登记**：文本中出现 `N1: xxx` / `N1：xxx` 定义模式（用户命令或**agent 回复**均挖）；
     b) **词典引导匹配**：已登记的短代号在后续文本中精确匹配（词边界校验），登记提及与状态。
- **状态机**：`定义 → 进行中 → 完成 / 变更`。基于提及上下文关键词判定（完成/done/收口/验收→完成；变更/调整/修改→变更；开始/执行/推进/继续→进行中），LLM 提取结果亦可携带 status 直接更新；
- **定义可更新**：后续再次出现 `N1: 新内容` 定义式重述时，最新定义生效（首见时间与定义节点保留首次记录，状态节点记录最近更新）；
- 词典项：代号、当前状态、定义、首次出现（时间+节点）、最近更新（时间+节点+上下文摘录）、出现次数；
- **来源覆盖**：用户命令 + agent 回复（TurnEnd/assistant 文本）+ LLM 摘要提取，三路并集；
- 状态关键词带否定检测（"尚未完成"不算完成）；定义句自身不触发状态流转；技术词汇停用表（S3/EC2/V2/Q1 等）阻止短码误登记；
- 升级/迁移时对历史节点做带版本号的重放（完成后才落标记，崩溃自动重跑），存量数据即时点亮；
- **已知限制（M2 规划）**：词典按代号名全局唯一——不同项目复用同名短码（两个项目都有 N1）时后者定义覆盖前者；M2 做按项目命名空间。

### 3.3b 阶段锚点（F3b，2026-07-26 新增）

- 每个节点由 LLM 归类 `kind ∈ {需求, 任务, 调研, 学习, 决策, 修复, 其他}`（规则引擎按关键词兜底），以彩色标签展示；
- 支持按 kind 过滤时间线——满足"以产研开发/调研学习的重要节点为锚点、以时间为轴"的浏览方式；
- 词典总览入口：面板头部打开代号词典面板，按最近更新排序展示 代号+状态+定义，点击跳转定义节点。

### 3.4 摘要引擎（F4）

- `SummaryEngine` 三实现，按设置与可用性选择：
  1. **CLISummarizer**（默认）：调用本机 `claude -p`（或 `codex exec`）headless，输出严格 JSON：`{title, keyPoints[], codenames[{name, definition}], resultLine}`；
  2. **ProviderSummarizer**：OpenAI-compatible `/chat/completions`；
  3. **RuleSummarizer**（兜底）：prompt 首句截断为标题、正则提代号，无 LLM 依赖；
- 摘要结果按命令内容 hash 缓存于 SQLite，不重复调用；
- 串行队列 + 限速，避免打爆本机 CLI。

### 3.5 窗口与交互（F5）

- 入口：mac menu bar（NSStatusItem）/ win 系统托盘；可选隐藏 Dock 图标（纯挂件模式）；
- 半透明浮窗：hover → 约 95% 不透明；失焦 → 约 25%（两档均可在设置中调节）；过渡带动画；
- always-on-top 开关（mac：window level floating；win：Topmost）；
- 非激活面板：点击 timeline 不抢占当前 app 焦点（mac NSPanel nonactivatingPanel）；
- 全部展示文本支持鼠标划选与复制；
- 窗口位置与尺寸记忆。

### 3.6 设置界面（F6）

- 摘要引擎：CLI（自动探测本机可用 CLI）/ 自定义 provider（base URL、API key、model）/ 纯规则；
- 透明度两档数值、always-on-top、Dock 图标显隐、回填天数；
- Agent 开关：Claude / Codex / Kimi / zcode（zcode 需填 session 路径）。

## 4. 非功能需求

- 双端视觉统一：共享 `design/design-tokens.json`（色板/字号/间距/圆角/透明度），两端原生实现各自渲染；系统特有交互（mac 毛玻璃、win Mica）保留；
- 性能：解析增量化；UI 万级节点可滚动（懒加载）；空闲 CPU 占用近零；
- 隐私：session 内容全部本地处理；仅在用户配置 provider 时才有网络请求（或 CLI 自身的网络行为）；
- 无沙盒直接分发 .app（M1 不做公证）。

## 5. 里程碑

- **M1 mac MVP（本轮交付）**：F1–F5 全部 + F6 基本项；真实 session 数据渲染验证；
- **M2**：搜索过滤增强、多项目视图、代号词典管理界面；
- **M3**：Windows WinUI 3 版（本轮交付可编译源码工程 + 共享解析规范，用户在 win 调试）。
