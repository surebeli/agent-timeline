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

## 3. L2 文本规整（v2，已过三方独立审查）

> **审查状态**（2026-07-27）：语料实证 / 产品设计 / 工程实现 三方独立审查，
> 结论均为 approve-with-amendments。本节为吸收全部 amendment 后的 v2 规则表，
> 链接规则按语料实证的 reject 意见重写（覆盖率 85.9% → 99.5%）。

### 3.1 作用点与档位

规整器是 Core 层纯函数 `TextNormalizer.Normalize(text, profile)`，三档：

| profile | 作用点 | 规则集 |
|---|---|---|
| **Excerpt** | 结果行派生（win `ParserUtil.ResultExcerpt`；mac `AppDelegate` 的 `.assistantText` 分支） | 全部规则；块级 skip 生效 |
| **Summary** | 规则摘要的标题/要点**展示文本**（win `RuleSummarizer`；mac 同名） | 同 Excerpt，但**围栏只保护不删除**（命令侧 skip 会整段清空用户贴的 spec）；行首列表/引用前缀剥除 |
| **Mining** | 代号词典的 `lastContext` 摘录展示 | 仅 inline unwrap（窗口仅 ~44 字符，块级 skip 会掏空） |

**不作用于**：`nodes.text`（命令原文）、`TaskComplete.FullText`（代号挖掘输入）、
`SummaryJson.BuildPrompt`（LLM 本就吃 markdown）。

### 3.2 管线顺序（顺序即正确性，实现必须照此）

1. 行尾归一：`\r\n` 与孤立 `\r` → `\n`（**仅此两种**，不碰 U+2028/0085 等，双端才对得齐）
2. ANSI strip（CSI `\x1b\[[0-9;]*[A-Za-z]`）
3. 逐行状态机：围栏判定 → 表格块 / 水平线 skip → 行首标题 unwrap
4. 行内保护：把行内 `` `code` `` 替换为占位符
5. 行内变换：链接/图片/旧版引用 convert → 强调 unwrap
6. 回填保护内容（**verbatim，不再过任何规则**——`` `**not bold**` `` 必须原样）
7. 空行折叠（≥3 个 `\n` → 2 个）→ 交给 `ResultExcerpt` 取首段 → `Clip`

### 3.3 规则表

| 标记 | 规则（v2） |
|---|---|
| 行内 `` `code` `` | **unwrap**，仅解**同行成对**：正则 `` `([^`\n]+)` ``（禁 Singleline）。落单反引号原样保留（PowerShell `` `n `` 转义 58 处） |
| 强调 `**…**` `__…__` `~~…~~` | **unwrap**，正则 `\*\*(?=\S)([^\n]+?)(?<=\S)\*\*`（**禁跨行**：语料 29 处跨段误配）；`**` 紧邻 `/` 时不视为标记（glob `src/**/*.ts`） |
| `*斜体*` `_下划_` | **keep**（`_snake_case_` 在 claude 1863 / codex 4253 条命中，纳入必伤正文） |
| 行首 `#{1,6} ` 标题 | **unwrap**，**必须有尾随空格**（`#include`/`#!/usr/bin/env`/`#region` 53 处不得误伤，全量零假阳性） |
| ``` 围栏 | **skip**（Excerpt 档），逐行状态机**禁用正则**（无闭合围栏上 `[\s\S]*?` 是 O(n²)）。四条约束：① 仅 skip **已闭合**围栏，未闭合的开围栏行按普通行；② 闭合识别容差 ≤ 开围栏缩进 +3（列表内围栏缩进 2/3/≥4sp 各 390/238/190 次）；③ info string 为 `text`/`md`/`markdown`/空 时**不 skip**（这类本就是正文）；④ 删除后以 `\n` 拼接，防前后句粘连 |
| 表格 | **skip**，判据钉死为**行首尾锚定** `^[ \t]*\|.*\|[ \t]*$`（实测 20213 命中 / 孤立命中 0，零假阳性）。宽松「含竖线即跳」会多杀 1599 行正文（JSON 枚举串 `需求\|任务\|…`、JS `a \|\| b`），**禁止**。分隔行 `\|---\|` 由本条覆盖 |
| `[文字](target)` 链接 | **convert**（v2 重写）：先脱去可选 `<…>` 包裹与 `:line[:col]`/`#Lnnn` 后缀，再按 target 判定——路径谓词 `^(/?[A-Za-z]:[\\/]\|/\|\./\|\.\./\|file:)` 或 http(s) → **只留文字**；否则 **keep**（Kimi 104 条日志假阳性 100% 挡住）。覆盖率 85.9%→99.5%，且 mac 端不再因「盘符」谓词失效而全灭 |
| 图片 `![alt](target)` | **convert**：整体消费（含前导 `!`）→ 留 alt，防悬空叹号 |
| `【F:path†Lxx】` | **convert** → `path:line`（与链接规则产出同形；范围 `†L12-L20` 取起始行）。判据收紧到 `†L\d+`，避开 5 条字面占位符 |
| ANSI `\x1b[…m` | **strip**（L1 后仍有 31 条携带，防御保留；零误伤） |
| `- ` / `1. ` / `> ` 前缀 | **Excerpt 档 keep**（首行剥除、其余保留：结果行已有 `→ ` 渲染前缀，`→ - 首项` 双前缀观感差）；**Summary 档全剥**（UI 已有 `·` 前缀）。⚠ Phase B **必须删除** `RuleSummarizer.StripMarkdownNoise`——它现在剥 `-`/`>` 却剥不掉 `1. `，与本条冲突且会吃 `--force`/`-> ` |
| `---` / `***` / `___` 水平线 | **strip**，整行正则 `^\s{0,3}([-*_])(?:\s*\1){2,}\s*$`，**必须排在强调规则之前**（`***` 否则被啃成 `*`）、**围栏保护之后**（front-matter 内的 `---` 不能剥）。收益实测最高：36 条 claude 结果行首段就是光秃秃的 `---` |
| 行尾双空格 / 行尾空白 | **convert**：逐行 TrimEnd（codex 510 处） |
| 裸 URL、`${var}`、`<占位符>`、全角【…】强调、`[text][ref]` 引用式链接、HTML 实体 | **keep**（原则 2/4；`<占位符>`/泛型 1459 条是正文） |
| `<br>` / `<br/>` | **convert** → `\n`（极窄白名单，显式列出即是对原则 2 的遵守） |
| 行尾归一 | **convert**：仅 `\r\n` 与孤立 `\r` → `\n`（枚举写死，否则 .NET `ReplaceLineEndings` 与 Swift 直觉实现产出不同） |

### 3.5 展示完整性：存储护栏 + 三级渐进披露（2026-07-27 落地）

L2 规整是**有损**变换，L3 钳制是**无损**的。历史上两者被混在一起——存储期就按
排版尺寸截断（标题 40 字写进库），原文再也拿不回来。现在拆开：

1. **存储层只留护栏**（§5.1 P4）：数值大到正常内容永不触碰，双端同表；
2. **折叠态**：`lineLimit` 钳制，观感与改造前逐像素一致（标题 1 行、要点摘要
   1 行带 `+n`、结果行 1 行）；
3. **展开态**：整条点击即解除全部钳制——完整标题、逐条要点列表、完整结果行；
4. **hover tooltip**：不必展开也能读全（派生标题 / 要点摘要行 / 结果行三处挂
   `.help()`，不占版面、不抢焦点）。

仍不可达的部分（诚实记录）：结果行最多是 agent 回复的**首段 500 字**，
`TaskComplete.FullText` 不落库，要拿"完整原始回复"必须先加 `nodes.full_text`
列——与 Phase D 富文本渲染同属一个前置约束（§5.2-1、Phase D 说明）。

### 3.4 总则（审查追加，全部为硬性）

1. **永不写空串**：任一 skip 导致输出为空时，回退到未规整的 `ResultExcerpt`；
   `Store.SetResultLine` 入口再加一道 `IsNullOrWhiteSpace → return`。
   实测 12 条结果行会被清空，叠加 Kimi 多 ContentPart × 无条件 UPDATE，
   会把已显示的结果行抹掉——**这是审查确认的唯一 UI 可见回归**；
2. **扫描预算**：逐行状态机 + 「凑够首段即停」早停；硬上限 32KB
   （实测 p99 3.8~5.9KB、max 37.8KB，无需为 100KB 设计）；
3. **幂等**：`normalize(normalize(x)) == normalize(x)` 作为断言；
4. **长度计量**：规整层只做形态变换，截断留给各端既有函数
   （win `Clip` 数 UTF-16、mac `truncate` 数 grapheme）；golden 样例的截断边界不落在非 BMP 字符上。

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

- **Phase A ✅ 已完成（Windows，2026-07-27）**：L1 堵漏——Claude 七前缀 + 命令块双字段序
  convert、Codex 技能回显 convert、CLI 摘要 stdin UTF-8 修复；库内 56 条历史泄漏节点已清除；
- **Phase A' ✅ 已完成**：§4 分叉两处——win 补 Kimi ContentPart 结果通道（TurnEnd payload
  实测 40/40 为空）、摘要 prompt 输入按 4000 截断；
- **Phase B ✅ 已完成（Windows）**：`Core/Text/TextNormalizer.cs` 三档纯函数（逐行状态机、
  32KB 预算），接入结果行派生 / RuleSummarizer / 词典 lastContext 三处作用点；删除
  `StripMarkdownNoise`；`Store.SetResultLine` 空串兜底；golden 基准
  `docs/normalize-cases.tsv`（48 例）+ 幂等断言，CoreSmokeTest 225 全绿；
- **Phase C（下一步，mac）**：按本文 §3 v2 移植 `TextNormalizer`、接同样三个作用点、
  读同一份 `docs/normalize-cases.tsv` 断言；同时拉平 §4 分叉（优先级见下）；
- **Phase D（已列入待办 = README Roadmap **M5**，2026-07-27）**：结果详情富文本渲染
  （代码块/表格/可点链接）。
  ⚠ 前置约束：L2 是不可逆有损变换、`FullText` 不落库、`TaskComplete` 无 source_offset，
  历史节点无源可依 —— Phase D 要么只对新数据生效，要么先加 `nodes.full_text` 列。
  排期判断：置于 M2（搜索/词典管理）之后。「看不全内容」已由 §3.5 三级渐进披露缓解，
  富文本属锦上添花；而 `nodes.full_text` 一旦落库即为不可回退的存储承诺（库体积随
  agent 回复原文线性增长），宜等搜索需求把「要不要存原文」一并逼出答案时再定。
  连带收益：该列可同时消除 §5.2-1 记录的重放损失（重放改吃原文而非已规整文本）。

### 5.1 mac 移植清单（Phase C，按产品损害排序）

| 优先级 | 项 | 说明 |
|---|---|---|
| **P0** | slash 命令 strip → convert | mac 现把 `<command-name>` 整条丢弃，**171 次用户命令根本不产生节点**——README 承诺「你提交过的每条命令」，丢节点比文本不整洁严重一个量级 |
| **P1** | resultLine 语义对齐 | mac 现为「全文拍平截 160」，应改为「规整 → 首段 → ≤500」，与 win 一致 |
| **P2** | TextNormalizer 移植 | 逐条按 §3 v2；块级判定用逐行状态机（Swift 端约 30 行）；`NSRegularExpression`(ICU) 不支持可变长 lookbehind，本文所用正则均在共同子集内；`\x1b` 在 Swift 写 `\u{1B}` |
| **P3** | L1 增量项 | mac 已有 10 个 ignoredPrefixes，需补 `<task-notification>`；`<system-reminder>` 防御性成对剥除 |
| **P4** ✅ | 常量统一 | **已完成（2026-07-27）**：改为「存储只留护栏、显示层负责钳制」——常量不再是排版决策，双端同表（`Core/DisplayLimits.{swift,cs}`：标题 120 / 要点 200×6 / 代号定义 120 / 结果行 500 / prompt 4000）。定档依据是 mac 431 节点实测（标题 p50=14 / p90=25 / max=41；旧 mac 40 只在 0.7% 触发、旧 win 20 会咬掉约 15%），护栏取 max 的 3 倍。**两端折叠态 UI 零变化**，完整内容由三级渐进披露给出（见 §3.5）。长度计量口径差异（win UTF-16 / mac grapheme）在护栏水位下不可能触发 |
| **P5** | 互抄单端优势 | mac→win：`attachment.queued_command` 补录；win→mac：codex JSON 候选提取、时间戳容错回退 |

### 5.2 已知未决（诚实记录）

1. **重放路径挖的是规整后的 resultLine**：`TaskComplete.FullText` 不落库，
   `ReplayCodenames` 只能读库里已规整的结果行。实测影响很小（定义式正则本就容忍
   `**N1**:`、dash 正则的 `\b` 在反引号旁仍成立），唯一损失是「定义句写在围栏/表格里」
   的重放场景。接受此差异；若将来要消除，加 `nodes.full_text` 列；
2. **旧数据不迁移**：已存库的旧 resultLine 保持原样，新数据走新规则。UI 无副作用
   （展示层不解析 markdown）；`CodenameReplayVersion` **不要**为此 bump——重放读的是
   库里已存文本，bump 只会在混合数据上空跑一遍；
3. **agent 结果侧的注入块**：L1 前缀过滤只作用于命令通道，若结果文本混入
   `<system-reminder>` 类标签目前无拦截。本机语料 0 命中，暂不处理。
