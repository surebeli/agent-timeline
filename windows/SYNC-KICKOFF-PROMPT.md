# Windows 同步开工 Prompt（Phase C'）

> 用法：在 Windows 机器的仓库根目录启动 agent 会话，把下面整段粘贴为首条指令。
> 清单来源见 `docs/TEXT-NORMALIZATION.md §5.3`（每条都经双端实跑验证）。

---

你在一台 Windows 11 机器上，当前目录是仓库 agent-timeline 根目录（远端
github.com/surebeli/agent-timeline，先 `git pull` 到最新 main）。产品是双端桌面挂件
"Agent Timeline"，两端已各自实机验证并跑在 CI 四道关下。

mac 端刚完成 Phase C（文本规整移植 + P0 丢命令修复 + 常量护栏化），随后一轮系统性
双端审计确认了 **7 项 Windows 侧待同步项**——其中 W0 正在丢用户命令。你的任务是把
它们逐条落地。

## 必读（按序，动手前读完）

1. `docs/TEXT-NORMALIZATION.md` §5.3 —— **你的任务清单**（W0–W6，含落点与最小修法）；
   §4.2 —— 分叉全表与归属；§3.5 —— 「存储只留护栏 + 三级渐进披露」的分层原则；
2. `docs/SESSION-FORMATS.md` —— 解析规范（W0 涉及 Claude §1）；
3. `windows/DEBUG-PLAYBOOK.md` §0 —— 构建方式（**只用 msbuild，禁止 `dotnet build`**）与
   种子数据脚本；
4. 对照实现：每条清单项都注明了 mac 侧对应文件，改之前先读 mac 那份——它是本轮的
   参照基准（但**不要照抄语言细节**，C# 有自己的惯用法）。

## 执行规则

- **按 W0→W6 顺序推进**，每项独立 commit（中文 commit message，风格参考 `git log`）；
- 每项完成后必须：`msbuild windows\AgentTimeline\AgentTimeline.csproj /restore
  /p:Configuration=Release /p:Platform=x64` 编译通过 +
  `dotnet run --project windows/CoreSmokeTest -c Release` 断言全绿（当前 225 条，
  **每项应新增对应断言**，参考 mac 侧同名测试的覆盖点）；
- W0 必须有真实语料验证：`%USERPROFILE%\.claude\projects` 下统计
  `attachment.queued_command` 条数与产出节点数，像 mac 那样报出「N 条回显 → N 条节点」；
- 改到 `design/design-tokens.json`、`docs/` 里的**双端共享层**时**停下来报告方案**，
  不要单方面改（CI 有 tokens 同源硬门禁会拦，但语义分歧要人来定）；
- 阶段性 push，CI 四道关自动回归；
- §4.2 第 10/11 条（Kimi 裸 slash 阈值、项目名派生）**本轮不做**——那两条需要先定
  双端规范再同时落地，属于共同待定项。

## 最终交付

1. W0–W6 全部落地并 push，CI 全绿；
2. `docs/TEXT-NORMALIZATION.md` §5.3 逐条标记完成状态，§4.2 表中已拉平项移入 §4.1；
3. `windows/README.md` 更新记录追加本轮条目；
4. 汇报：完成项 / 实测数据（W0 的语料统计、W1 的重试行为验证）/ 新发现问题 /
   仍不一致的点。
