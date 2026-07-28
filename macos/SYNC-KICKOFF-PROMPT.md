# macOS 同步开工 Prompt（对齐 Windows 侧跨端合并审计发现）

> 用法：在 mac 机器的仓库根目录启动 agent 会话，把下面整段粘贴为首条指令。
> 清单来源见 `docs/TEXT-NORMALIZATION.md §4.2b`（每条都在 Windows 本机实跑实证）。

---

你在一台 macOS 机器上，当前目录是仓库 agent-timeline 根目录（远端
github.com/surebeli/agent-timeline，先 `git pull` 到最新 main）。产品是双端桌面挂件
"Agent Timeline"，两端已各自实机验证并跑在 CI 四道关下。

Windows 侧刚做完一轮**跨端合并审计**——因为 mac 端 v0.4.1 修改了大量 Windows 文件，
而 macOS 上只能跑跨平台冒烟（无法编译 WinUI、无法用 Windows 路径语义验证、无法跑
Windows 本机的四家真实语料）。审计用本机 1681 codex / 862 claude / 187 kimi / 46 zcode
真实语料 + 临时库端到端跑出 **4 个已修缺陷（A1–A4）+ 1 个双端共有待定项（B1）**，
其中 **A1 正在污染用户时间线**。你的任务是把它们在 mac 侧同步。

## 必读（按序，动手前读完）

1. `docs/TEXT-NORMALIZATION.md` §4.2b —— **你的任务清单**（A1–A4 已修项 + B1 待定项 +
   「已记录不修」的可达性说明，避免重复审）；
2. 对照实现：Windows 侧 commit `b977e10`（`git show b977e10`）—— 每条的最小修法与
   注释里的实证数据都在里面。**不要照抄语言细节**，Swift 有自己的惯用法；
3. `docs/SESSION-FORMATS.md` §3（Kimi）与 §2（Codex）—— A1/A2 涉及的解析规范。

## 执行规则

- **A1 最优先**（它正在把子 agent 的回复挂到用户命令节点上，并污染代号词典）；
- 每项独立 commit（中文 commit message，风格参考 `git log`）；
- 每项完成后 `swift test` 全绿，**每项应新增对应断言**（参考 Windows 侧
  `windows/CoreSmokeTest/Program.cs` 里同名测试的覆盖点，当前 315 条）；
- **A1 需要真实语料验证**：mac 上若有 `~/.kimi-code/sessions`，统计 `agents/main` 与
  `agents/agent-N` 的文件数与各自产出的 TaskComplete 数，像 Windows 那样报出
  「N 个子 agent 文件 → 0 命令 + M 条回复 → K 个节点结果行被错配」；没有语料就如实说明；
- **B1 是双端共有设计缺陷，需要你先给方案再落地**：Windows 侧建议方案 (a)（流式只应用
  本文件第一条 session_meta），但它有语义变化（被 resume 的会话将按 rollout 各自成会话，
  当前是合并的）——**请先评估这个语义变化对 UI「每会话最后一条 assistant 消息」的影响，
  把结论写进 §4.2b 并报告，确认后再双端同时落地**；
- 改 `design/design-tokens.json`、`docs/` 里的**双端共享层**时停下来报告方案；
- 阶段性 push，CI 四道关自动回归。

## 最终交付

1. A1–A4 全部落地并 push，CI 全绿；B1 给出方案与影响评估（不擅自落地）；
2. `docs/TEXT-NORMALIZATION.md` §4.2b 逐条标记完成状态；
3. `macos/` 侧的更新记录（或 CHANGELOG）追加本轮条目；
4. 汇报：完成项 / 实测数据（A1 的语料统计）/ 新发现问题 / B1 的方案与理由。
