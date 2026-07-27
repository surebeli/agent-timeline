# 时间线文本规整方案（提案 v1，2026-07-27）

> 背景：时间线展示的「用户命令」与「结果行」里混入了各家 agent 的标记内容——harness 注入的
> XML 风格标签、markdown 结构、引用/回显格式。本方案基于**本机四家 agent 真实语料普查**
> （Claude 30 文件 / Codex 25 文件 / Kimi 31 + zcode 38 文件，2026-07-27）+ 官方源码与文档
> 交叉验证（openai/codex protocol.rs、MoonshotAI/kimi-cli message.py、Claude Code hooks 文档
> 与两个第三方 transcript 解析器）+ 双端解析器现状盘点。
>
> **状态**：L1 解析层堵漏已在 Windows 端落地（见 §5）；L2 规整层为**提案待确认**，确认后
> 双端同步实施。

## 0. 设计原则

1. **命令原文是产品灵魂，`nodes.text` 永不改写**——L2 规整只作用于*展示态派生物*
   （resultLine 存库值、规则摘要的标题/要点提取输入）；`FullText` 保留原文供代号挖掘；
2. **剥离必须白名单化**：语料里存在大量「长得像标签的正文」——代码泛型 `Option<String>`、
   占位符 `<port>`/`<severity P0/P1/P2>`（Claude 语料 60 处、Kimi/zcode 66+15 处）。
   任何「剥一切尖括号」的泛化策略都会吃掉正文；
3. **先摘围栏再匹配**：markdown/XML 剥离逻辑先把行内反引号与 ``` 围栏内文本摘除保护，
   处理后回填——否则 zcode/kimi 语料大量误伤；
4. **md-link 识别必须验 target 形态**（URL / 盘符路径 / file:）：Kimi 语料 78 处
   `行号: [ts][pid][A][hex](Func)` 日志与链接正则完全同形，无 target 校验则 78/78 全是假阳性。

## 1. 分层模型

| 层 | 位置 | 作用对象 | 性质 |
|---|---|---|---|
| **L1 注入块过滤** | 解析层（各 Parser） | 整条非人类输入 | 已定语义（mac 既有 + 本次堵漏），双端应一致 |
| **L2 文本规整** | 解析层出口（共享 TextNormalizer） | resultLine 存库值、规则摘要提取输入 | **本提案核心，待确认** |
| **L3 视觉钳制** | 显示层 | 折叠行数/省略号 | 现状已有，不动 |

## 2. L1 注入块过滤（整条 strip / convert）——已实施

| agent | 规则 | 语料证据 | 处置 |
|---|---|---|---|
| Claude | `<task-notification>` 前缀 | **793 次**（此前全部以"用户命令"泄漏，最大漏源） | strip |
| Claude | `<local-command-stdout>` 前缀（内含 ANSI） | 96 次 | strip |
| Claude | `Caveat:` / `[Request interrupted` / `This session is being continued from` 前缀 | 若干 | strip（对齐 mac 既有清单） |
| Claude | 命令回显块**两种字段序**（`<command-name>` 先 111 次 / `<command-message>` 先 **60 次**——后者此前整批漏网） | 171 次 | convert → `/name args`（非空 `<command-args>` 是用户真实输入，拼回正文） |
| Claude | `<local-command-caveat>` / `<system-reminder>` 前缀 | 已被 isMeta/tool_result 双重覆盖 | strip（双保险保留） |
| Codex | `<user_instructions>` / `<environment_context>` 前缀 | 时间线字段 0 命中（存在于非时间线行） | strip（防御保留）；protocol.rs 另有 10 个注入标签名可做白名单扩充 |
| Codex | `[$plugin:skill](本机 SKILL.md 路径)` 开头 | **17/70 条用户消息** | convert → 留 `$plugin:skill` 徽标文字，剥本机路径（跨机无效且泄漏用户名） |
| Kimi | `<system-reminder>` 独立消息（kimi-cli 官方语义："authoritative system directives"） | 本机语料 0 命中 | strip（预防；判定逻辑可抄 kimi-cli `is_system_reminder_message`） |
| Kimi/zcode | XML 注入标签 | **两家语料均零检出**，无需前缀过滤 | — |

## 3. L2 文本规整（提案，待确认）

作用点：① 四家 parser 的 resultLine 在 `ResultExcerpt` 前先过 `TextNormalizer.ForExcerpt`；
② `RuleSummarizer` 标题/要点提取输入。**不作用于 nodes.text**。

| 标记 | 语料密度 | 规则 |
|---|---|---|
| 行内 `` `code` `` | Claude 4875 / Codex 1856（91% 结果行命中）/ zcode+kimi 2317 | **unwrap**：去反引号留内容 |
| `**加粗**` | 1195 / 58 / 314 | **unwrap**：去星号留内容 |
| 行首 `#{1,6} ` 标题 | 447 / 105 / 287（zcode 结果行常以 `# 报告标题` 开头，时间线出现裸 `#`） | **unwrap**：剥行首井号+空格（标题文本本身是最好的摘要素材） |
| ``` 围栏 | 34 / 124 / 117 | **skip**：摘录时跳过整个围栏段取正文行 |
| 表格行 `\|…\|` | 126 / 337 / 37 | **skip**：摘录时跳过（Codex 结果行 30% 含表格） |
| `[文字](target)` 链接 | 39 / 265+46 / 2 | **convert**：target 为本机路径 → 只留文字（Codex 新版 file citation `[file.rs:42](C:\abs\path)` 265 处即此类）；target 为 http(s) → 留文字（富文本期可点击）；target 非 URL/路径形态 → **原样保留**（防 Kimi 日志假阳性） |
| `【F:path†Lxx】` 旧版 Codex 引用 | 本机 0 命中（官方规范存在，旧语料/云端会话可能出现） | **convert** → `path:Lxx` 纯文本 |
| ANSI `\x1b[…m` | 64（全在 local-command-stdout 内，L1 后自然消失） | **strip**（正则 `\x1b\[[0-9;]*[A-Za-z]`，防御保留） |
| `- ` / `1. ` 列表前缀、`> ` 引用 | 1312 / 1401 / 若干 | **keep**（纯文本形态可读） |
| `---` 水平线 | zcode 13 | **strip**（整行无信息量） |
| 裸 URL、`${var}`、`<占位符>`、全角【…】强调 | 各处 | **keep**（全部是正文，见原则 2/4） |
| CRLF 混排 | 20+ | **convert**：统一 `\n`（现有 ReplaceLineEndings 已覆盖大部分路径） |

## 4. 双端一致性与既有差异

解析器现状盘点发现的 win/mac 分叉（规整层落地前需拉平的部分，完整清单见普查记录）：

1. resultLine 语义：win=首段≤500（2026-07-27 起）/ mac=全文拍平截 160 → **mac 待同步首段语义**；
2. `<command-name>`：win=convert 成 `/xxx` 节点 / mac=strip 整条 → 建议 mac 对齐 convert（含双字段序与 args）；
3. Kimi 结果通道：**TurnEnd payload 实测恒为空 `{}`**（40/40），mac 走 assistant ContentPart
   通道 / win 探测 TurnEnd 四个 key 永远拿不到 → **win 待补 ContentPart 通道**；
4. win LLM prompt 无输入截断（mac 4000）→ win 待补；
5. 截断常量散落且不一致（标题 win40/mac60、要点 win60×5/mac80×6、规则标题 win20/mac40…）
   → 规整层落地时列常量迁移表统一；
6. mac 的 `attachment.queued_command` 补录、win 的 codex JSON 候选提取，互为单端优势 → 互抄。

## 5. 实施计划

- **Phase A（已完成，Windows）**：L1 堵漏——Claude 七前缀 + 命令块双字段序 convert、
  Codex 技能回显 convert、CLI 摘要 stdin UTF-8 修复；CoreSmokeTest 覆盖（116→123 断言）；
- **Phase B（待确认后做）**：共享 `TextNormalizer`（Core 层纯函数 + 冒烟基准样例），
  接入 resultLine 与 RuleSummarizer；§3 规则表即验收标准；
- **Phase C**：mac 同步（L1 增量项 + TextNormalizer 移植 + §4 分叉拉平），swift test 对齐断言；
- **Phase D（远期可选）**：结果详情富文本渲染（代码块/表格/可点链接），届时 L2 输出双形态。
