# macOS Phase C 任务书（双端对齐：丢命令修复 + 文本规整移植）

> 复制「开工 prompt」一节给 mac 机器上的 agent 会话即可开工。
> Windows 端对应工作已全部完成并推送（截至 commit `12eb704`）。

## 开工 prompt

你在一台 macOS 机器上，当前目录是仓库 agent-timeline 根目录（远端 github.com/surebeli/agent-timeline，**先 git pull 到最新 main**）。产品是双端桌面挂件 "Agent Timeline"。Windows 端刚完成一轮「时间线文本治理」，本轮任务是把 mac 端拉平——**其中 P0 是一个正在丢用户数据的缺陷**。

必读（按序，动手前读完）：
1. `docs/TEXT-NORMALIZATION.md` —— 本轮的规范总纲。§3 是经三方独立审查（语料实证/产品设计/工程实现）定稿的 v2 规则表，§5.1 是给你的移植清单（已按产品损害排序），§5.2 是已知未决项；
2. `docs/normalize-cases.tsv` —— 48 条 golden 基准，**双端共享的单一事实源**，你的实现必须读同一份文件断言（与 design-tokens.json 同源文化一致）；
3. `windows/AgentTimeline/Core/Text/TextNormalizer.cs` —— Windows 参考实现（纯函数、逐行状态机），逐条按 §3 移植，不要照抄语言细节；
4. `docs/SESSION-FORMATS.md` §1/§4 —— 解析规范（§4 zcode 已按实机样例落笔，mac 侧解析器仍是占位）。

按 §5.1 的优先级推进，**P0 先做完并单独提交**：

- **P0 丢命令修复（最高优先，这是数据缺陷不是观感问题）**：mac 的 `AgentSessionParser.ignoredPrefixes` 把 `<command-name>` 整条丢弃，导致用户的 slash 命令**根本不产生时间线节点**（Windows 侧同口径语料实测 171 次）。README 承诺「你提交过的每条命令」，丢节点比文本不整洁严重一个量级。改为与 win 同语义的 convert：识别命令回显块的**两种字段顺序**（`<command-name>` 先 / `<command-message>` 先，后者占 60/171），产出 `/name args`（非空 `<command-args>` 是用户真实输入，拼回正文）。参考 `windows/AgentTimeline/Core/Parsers/ClaudeParser.cs`；顺带补 `<task-notification>` 到忽略前缀（win 侧实测 793 次泄漏）。
- **P1 resultLine 语义对齐**：mac 现在是「全文拍平截 160」（`AppDelegate.swift` 的 `.assistantText` 分支），改为「规整 → 取首个非空段落 → ≤500，代理对安全截断」，与 win `ParserUtil.ResultExcerpt` 一致。**必须带空串兜底**：规整后为空（整段是围栏/表格）时回退未规整文本，且 Store 写入口挡一道空白——这是审查确认的唯一 UI 可见回归（会把已显示的绿色结果行抹掉）。
- **P2 TextNormalizer 移植**：按 §3 v2 逐条实现三档（Excerpt / Summary / Mining），接三个作用点（结果行派生、RuleSummarizer 展示文本、代号词典 lastContext）。注意：块级判定用**逐行状态机不要用正则**（无闭合围栏上 `[\s\S]*?` 是 O(n²)）；`NSRegularExpression`(ICU) 不支持可变长 lookbehind，§3 所用正则均在共同子集内；`\x1b` 在 Swift 写 `\u{1B}`。移植同时**删除** `RuleSummarizer.swift` 里与之冲突的 bullet 前缀筛选逻辑（它把列表前缀当选行谓词，与 §3 的 keep/剥离规则冲突）。
- **P3–P5**（同一提交可合并）：L1 增量前缀；§4 常量统一（标题 win20/mac40、要点 win30×3/mac60×5 等，连同长度计量口径 win UTF-16 / mac grapheme，在 golden 里锁死）；互抄单端优势（mac→win 已知的 `attachment.queued_command` 补录保留，win→mac 补 codex JSON 候选提取与时间戳容错回退）。

铁律：
1. **命令原文永不改写**——`nodes.text`、代号挖掘输入不过规整器；规整只作用于展示态派生物（§0 原则 1）；
2. **剥离必须白名单化**——`Option<String>`、`<port>` 这类是正文，泛化剥尖括号必伤（§0 原则 2）；
3. **先摘围栏再匹配**、**md-link 必须验 target 形态**（§0 原则 3/4，后者防 78/78 假阳性）；
4. golden 用例只增不改：`docs/normalize-cases.tsv` 是双端契约，你若认为某条期望值错了，**先在报告里说明并等确认**，不要直接改。

交付：
1. P0 单独一个 commit（含 swift test 断言：两种字段序 + args 拼接 + task-notification 过滤）；
2. P1/P2 各自 commit，`swift test` 读 `docs/normalize-cases.tsv` 逐例断言 + 幂等断言（`normalize(normalize(x)) == normalize(x)`，`-noidem` 后缀用例跳过幂等）；
3. 全部中文 commit、push 后 CI 四道关全绿；
4. 汇报：完成项 / 与 win 行为仍不一致的点（如有，注明原因）/ 新发现问题 / 遗留项。

范围外（本轮不做，另有安排）：mac 端 zcode 解析器实现（`docs/SESSION-FORMATS.md` §4 规范已就绪，但属独立任务）；Phase D 富文本渲染。
