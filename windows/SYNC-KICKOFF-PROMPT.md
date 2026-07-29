# Windows 同步开工 Prompt（v0.5.1 轮 · 2026-07-29）

> 用法：在 Windows 机器的仓库根目录启动 agent 会话，把下面 `---` 之间的整段粘贴为首条指令。
>
> 上一轮（Phase C' / W0–W6）已全部完成，本文件整体替换为本轮内容；历史见 git log。

---

你在一台 Windows 11 机器上，当前目录是仓库 agent-timeline 根目录
（远端 github.com/surebeli/agent-timeline，先 `git pull` 到最新 main）。产品是双端桌面
挂件「Agent Timeline」，两端已各自实机验证并跑在 CI 四道关下，最新版本 **v0.5.1**。

**本轮 Windows 侧没有待写的产品代码**——mac 侧这轮的唯一功能改动（结果行「引子续接」）
已经同步写进 Windows 实现并过了 CI。你的任务是**两件只有真机能做的事**：验证与出图。

## 必读（动手前读完）

1. `docs/TEXT-NORMALIZATION.md` §3.3b —— 本轮功能改动的规范、四条硬约束与 mac 侧实测数据；
2. `windows/DEBUG-PLAYBOOK.md` §0 —— 构建方式（**只用 msbuild，禁止 `dotnet build`**，
   PRI 任务会报 MSB4062）；§3b —— 宣发截图拍摄规程，是任务 B 的规范；
3. `macos/scripts/shots/` —— mac 侧拍摄脚本三件套，是任务 B 的参照实现。

## 任务 A：引子续接的真机验证（先做）

改动落点（已在仓库里，不用你写）：`windows/AgentTimeline/Core/Parsers/IAgentSessionParser.cs`
的 `ResultExcerpt` / `LeadInJoined` / `Paragraphs` / `IsLeadIn`，以及
`Core/Text/TextNormalizer.cs` 的 `StripLeadingMarkers`。

**A1. 编译 + 冒烟全绿**

```powershell
msbuild windows\AgentTimeline\AgentTimeline.csproj /restore /p:Configuration=Release /p:Platform=x64
dotnet run --project windows/CoreSmokeTest -c Release
```

冒烟当前 **354 条**断言，其中 `ResultExcerptLeadIn()` 是本轮新增的 6 条。

**A2. 差分执行**——这是本轮真正要你做的事，CI 做不了。mac 侧在本机 2460 条真实 agent
回复上跑了改动前后对比，你在 Windows 语料上复现同样口径，逐项报出实测值：

| 指标 | mac 实测 | 你的值 |
|---|---|---|
| 产出变化条数 / 占比 | 350 / 14.2% | |
| **变短条数（回归）** | **0** | |
| **旧值是新值的前缀** | **全部成立** | |
| 冒号结尾条数 前→后 | 351 → 2 | |
| 平均长度 前→后 | 90 → 114 | |
| 空串 前→后（§3.4-1 不变式） | 0 → 0 | |

做法：从 `%USERPROFILE%\.claude\projects\**\*.jsonl`（assistant 文本块）与
`%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl`（`payload.last_agent_message`）
抽语料，用 `git stash` 切换改动前/后两个源码状态各跑一遍 `ParserUtil.ResultExcerpt`，
对比两份产出。

**「变短 0 条」和「旧值是新值的前缀」这两条必须成立**——它们从数据上证明本次改动
只可能加内容、不可能改内容。任何一条不成立都说明 C# 实现与规范有偏差，**停下来报告，
不要自行改规范**。

**A3. 装 CI 出的包**：Release 页 `AgentTimeline-windows-x64-v0.5.1.zip`，解压运行，
确认托盘常驻、时间线正常上屏、设置窗 caption 显示 `v0.5.1`。

## 任务 B：重拍 README「Windows 实机一览」三张图

现状：`docs/assets/screenshot-windows-{timeline,projects,dictionary}.png` 摄于四家 agent
时期（v0.4.x），@1x 小图（340×640 / 272×478 / 314×430），三张比例互不相同。
mac 侧那三张已统一为同画布 / 同缩放 / 同背板（1718×1352，README 三列同为 290 宽）。

**规范见 `windows/DEBUG-PLAYBOOK.md` §3b**，几何常量照抄：面板 640×580dip、
缩放 200%（1dip = 2px）、合成画布取三态并集 + 四边 96px、背板 `#101014` + 光晕 + 投影。

**B1. 先把拍摄脚本落到 `windows/scripts/shots/`**（PowerShell + C# 小工具），
对标 `macos/scripts/shots/` 三件套。mac 侧踩过的坑必须移植：

- **抓窗口，不要抓屏幕区域**：用 `PrintWindow` + `PW_RENDERFULLCONTENT`
  （或 Windows.Graphics.Capture 按 HWND）。屏幕区域截图会把盖在上面的第三方全屏浮层
  一起摄进来——mac 端旧图上的彩色光斑就是这么来的，当时一度误判成应用的半透明缺陷；
- **落点校验 + 重试**：mac 侧面板读不到保存的 frame 时会贴屏幕右缘，而贴右缘时
  词典弹层没地方向右展开、被系统挤回面板内，产出尺寸静默改变。Windows 侧确认等价行为，
  写完设置回读确认，落点不对就重试；
- **不变式拦截**：词典态抓取宽度必须**大于**时间线态（说明弹层确实溢出了面板），
  否则直接失败并提示把面板左移，而不是默默产出一套尺寸不一致的图；
- **浮层打开时不要走优雅退出**：模态菜单的事件循环会挡住退出消息，mac 上卡到超时两次。
  先关浮层，或直接 `Stop-Process -Force`。

**B2. 数据安全铁律**（§3b，逐条遵守，mac 侧每次拍摄都照做）：

1. **隐私红线**：公开截图绝不能出现真实时间线（真实项目名 / 命令原文），
   一律灌注 `windows\scripts\demo-seed.py`（已含 Grok 节点，12 条 / 5 agent × 4 项目）；
2. **数据安全**：先备份真实 db（含 `-wal`/`-shm`）与设置；换库用**文件级交换**，
   不要删目录（win 端曾因子目录被进程占用导致目录级还原失败）；拍完立即还原，
   并用还原前后 `select agent, count(*)` + 文件哈希**双重核验**；脚本用 try/finally
   兜底，中途失败或 Ctrl-C 也必须还原；
3. **隔离干扰**：演示配置 = 摘要引擎纯规则 + 全部 agent 监听关闭 + 回填 0 天；
4. 演示数据在场时间越短越好；任何一步失败**先还原再排障**。

**B3. 三态**：timeline（无浮层）/ projects（点开「全部」下拉）/ dictionary（点开代号词典）。
产出装进 `docs/assets/screenshot-windows-*.png`，README「Windows 实机一览」三列宽度
统一成 290，并把注记里「Windows 一览摄于四家 agent 时期」那句改掉。

## 执行规则

- 任务 A 与 B 分别独立 commit（中文 commit message，风格参考 `git log`）；
- **脚本要入仓，不能只留参数**——一次性工具下次没人复现得了（mac 侧这轮的教训）；
  且脚本必须**实跑验证过**再提交：跑不通的脚本等于没有。mac 侧实跑两轮才发现落点
  静默失效导致画布从 1718 缩到 1472；
- 改到 `design/design-tokens.json`、`docs/` 里的**双端共享层**时**停下来报告方案**，
  不要单方面改（CI 有 tokens 同源硬门禁能拦住 token 漂移，但语义分歧要人来定）；
- `docs/normalize-cases.tsv` 的 golden 用例**只增不改**——认为某条期望值错了，
  先在报告里说明并等确认；
- 阶段性 push，CI 四道关自动回归。

## 最终交付

1. 任务 A、B 落地并 push，CI 全绿；
2. A2 的实测表格填进 `docs/TEXT-NORMALIZATION.md §3.3b`（追加一行 Windows 实测，
   **不要改 mac 那行**）；
3. `windows/README.md` 更新记录追加本轮条目；
4. 本文件更新为下一轮内容，或在顶部标记本轮已完成；
5. 汇报：完成项 / A2 实测数据 / 新发现问题 / 仍不一致的点。**如实报**——
   数字对不上就说对不上，不要凑。

## 本轮不做

- `docs/TEXT-NORMALIZATION.md` §4.2c Grok 编排器派发任务书的过滤——需先定双端规范；
- 已入库结果行的回填——用户 2026-07-29 明确决定不回填，规则只对新节点生效。
