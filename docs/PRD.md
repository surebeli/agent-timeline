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

### 3.3 代号词典（F3）

- 自动识别：正则候选（如 `[A-Z][A-Z0-9]*(-[A-Z0-9]+)+`、`T-XXX-00` 等模式）+ LLM 提取结果的并集；
- 词典项：代号、首次出现时间、定义节点（首次出现的命令）、上下文摘录、出现次数；
- 后续节点复现该代号时自动关联。

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
