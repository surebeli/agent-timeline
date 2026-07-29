# macOS 同步开工 Prompt（多语言地基追齐轮 · 2026-07-29）

> ## ✅ 本轮已完成（2026-07-30）
>
> 任务 A/B/C 落地并 push，CI 第五关（Strings · 文案表同源）对 mac 那半边
> **已从「跳过」转为真校验并通过**（69 键 × 4 语言，副本同源）。mac 测试 49 → 56 项全绿。
>
> - **A** `build-app.sh` 增生成步，`design/strings.json` → `UI/StringsData.swift`，
>   与 `DesignTokensData` 同构、原样嵌入。删掉产物重跑整包构建验证过生成链路；
> - **B** `UI/Strings.swift`，逐条对齐 `AppStrings.cs` 的行为契约——平台覆盖 `@mac`、
>   跟随系统解析（zh-TW/zh-HK → zh-Hans，兜底 en）、`{0}` 序号占位符、
>   载入失败不抛、缺键回显键名、切换发 `didChangeNotification`；
> - **C** `AppSettings.language` 存字符串 rawValue，`AppDelegate` 在任何界面构建前载入。
>
> **任务 D 查证结论：mac 确实没有摘要缓存**——你没看漏。`Store.swift` 只建
> `nodes` / `codenames` / `file_offsets` 三张表，Windows 另有 `summaries`。PRD §3.4
> 在 mac 端从未实现，已如实记入 `docs/TEXT-NORMALIZATION.md §4.2` 分叉清单**第 20 条**
> （含「绝不能把语言混进命令 hash」那条硬约束的转述），本轮未补实现。
>
> **留给下一轮**：69 键接线 + 识别词表四语化（含你调研出的三条会让现有实现出错的发现），
> 按你的要求双端同轮做。
>
> 下面 `---` 之间是本轮的原始任务书，保留备查。

> 用法：在 macOS 机器的仓库根目录启动 agent 会话，把下面 `---` 之间的整段粘贴为首条指令。
>
> 上一轮（对齐 Windows 跨端合并审计，A1–A4 + B1）已全部完成并随 v0.5.0 发布，
> 本文件整体替换为本轮内容；历史见 git log。

---

你在一台 macOS 机器上，当前目录是仓库 agent-timeline 根目录
（远端 github.com/surebeli/agent-timeline，先 `git pull` 到最新 main）。产品是双端桌面
挂件「Agent Timeline」，两端已各自实机验证并跑在 CI 五道关下，最新发布 **v0.6.0**。

**本轮任务：把 mac 端追齐 Windows 端已落地的多语言地基。** Windows 侧已合入
（见 `e54fadf`），mac 侧对应实现尚缺。CI 的文案表同源关目前对 mac 那半边是「跳过」
而非「失败」——你把文件补出来之后，它会**自动开始校验**。

## 必读（动手前读完）

1. `design/strings.json` —— 双端共享文案唯一事实源，69 键 × 4 语言。**先读 meta 段**，
   里面写了占位符约定、平台覆盖约定，以及「kind/status 只是显示标签、落库值不变」
   这条最关键的约束；
2. `windows/AgentTimeline/AppStrings.cs` —— Windows 侧加载器，是你要对齐的参照实现。
   注释里写了每个取舍的**理由**，别只抄形；
3. `scripts/check-strings.py` —— CI 门禁的六项校验。补完 mac 侧先本地跑一遍；
4. `macos/scripts/build-app.sh` 第 16–24 行 —— `DesignTokensData.swift` 的生成方式，
   文案表的嵌入副本照这个套路做。

## 背景：为什么不用 .xcstrings

语言由**应用内设置**决定，不依赖系统资源解析；两端又有大量代码构建的 UI（chip 弹层、
词典面板、菜单栏菜单），原生资源在那些地方同样要手写查表。共享 JSON 能一份文件译四语
+ CI 硬校验键集合，与 `design-tokens.json` 同一套已验证范式。

建表时实测发现：**两端在只有中文的情况下就已经漂移 8 处以上**（纯规则「不调用 LLM」
vs「不调用模型」、退出 / 显隐 / 加载更多 / 项目过滤…）。单语尚且如此，四语各端各译必然
更糟。这就是这张表和那道门禁存在的理由——**mac 端今后不要再写任何界面字面量**。

## 任务 A：嵌入副本 + 生成步骤

`macos/scripts/build-app.sh` 里加一步，与 `DesignTokensData.swift` 完全同构：

```
design/strings.json → macos/Sources/AgentTimeline/UI/StringsData.swift
    enum StringsData { static let json = #"""…"""# }
```

CI 断言是「`design/strings.json` 的内容（rstrip 后）必须原样出现在该文件里」，所以
**不要重新格式化 JSON**，原样嵌入。

## 任务 B：`Strings.swift` 加载器（对齐 `AppStrings.cs`）

要点逐条，别自行发挥：

- **语言枚举**：`system`（默认）/ `zhHans` / `en` / `ja` / `ko`；
- **「跟随系统」解析**：按系统 UI 语言取最接近的一档；`zh-TW`/`zh-HK` 也归到
  `zh-Hans`——目前只有简体一份，给繁体用户简体好过直接掉英文；兜底 `en`；
- **平台覆盖**：查找时先试 `键名@mac` 再回退 `键名`。目前只有一个
  `header.hideToTray@mac`（macOS 是菜单栏应用，没有托盘）；
- **占位符**：`{0}`/`{1}` 序号式，`format` 与 Windows 的 `Format` **逐字同语义**。
  不要用 Swift 字符串插值——同一份表在另一端就成了字面量；
- **两处容错必须照做**（Windows 侧注释里写了理由）：
  1. 载入失败**不抛**，退化成「键名原样显示」。挂件不该因为一份文案表读不动就起不来；
  2. 查不到键时**回显键名，不返回空串**。空串会让界面看起来「少了个控件」，键名则一眼
     看出漏了哪个键——这类缺失只有跑起来才暴露，得让它自曝；
- **切换即时生效**：需要一个变更通知，供代码构建的 UI（菜单栏菜单等不会自动刷新）重建。

## 任务 C：`AppSettings.language`

存**字符串**不存 int（`"System"/"ZhHans"/"En"/"Ja"/"Ko"`）：设置是人可读的，将来加语言
时不希望旧值语义被序号挪位。Windows 侧同名同值，两端设置语义保持一致。

## 任务 D：确认一处双端分叉（**先查证再动手**）

Windows 侧本轮给摘要缓存键加了语言维度，防止切成英文后重复命令命中旧的中文摘要。
但我读 `macos/Sources/AgentTimeline/Core/Store.swift` 时只看到 `nodes` / `codenames` /
`file_offsets` **三张表，没有 summaries 缓存表**——而 PRD §3.4 写着「摘要结果按命令内容
hash 缓存于 SQLite」。

请你确认：**mac 端到底有没有实现摘要缓存？**（我是从 Windows 机器远看的，可能看漏。）

- 若**没有**：这是一处既有的双端分叉（mac 未实现 PRD §3.4 的缓存），请如实记进
  `docs/TEXT-NORMALIZATION.md §4.2` 的分叉清单，本轮**不必补实现**；
- 若**有**（我看漏了）：照 Windows 的做法给缓存键加语言，并注意那条硬约束——
  **绝不能把语言混进命令 hash**，它参与 `UNIQUE(agent, session_id, ts, command_hash)`，
  改了重扫必产生重复行（2026-07-28 W-e 那轮刚踩过同类坑）。只在查缓存这一处派生新键，
  `zh-Hans` 不加后缀以兼容存量行。

## 本轮不做（下一轮双端同步做，别提前动）

- **69 个键接线**：两端要一起替换硬编码，等 mac 地基就位后同轮做，避免两端界面不同步；
- **识别词表四语化**：`CodenameRegistry` 的状态关键词、`RuleSummarizer` 的分类关键词。
  这块已做过日/韩术语调研（各自建了真实语料），结论里有三条**会让现有实现出错**的发现，
  必须双端一起改：
  1. **否定检测的位置三语不同**——中文前置（尚未完成）、日语后置（完了して**いない**）、
     韩语两头都有（**안** 완료 / 완료하**지 않**았다）。现有「关键词前两字符」逻辑对日韩
     完全不适用；且中文否定字集 `未没不别无非` 在日语里是普通汉字（`不具合`/`非表示`），
     直接复用会**误伤**——`不具合を修正完了` 会被判成否定；
  2. **`ClauseSeparators` 缺 ASCII 句点 `.`**——韩语句子 100% 用 ASCII 句点结尾，子句窗口
     永远不会在句号处切断，只会撞上长度上限，邻句状态词大量串味；
  3. **`TextNormalizer` 缺 NFKC 归一化**——日语全角英数 `ＷＩＰ`、半角片假名、分离浊点会
     让子串匹配整个失效。**这条影响双端**，mac 侧对应
     `precomposedStringWithCompatibilityMapping`。

  这三条已记录在案，你本轮**不要单方面改**——它们要双端同时改才不会造成新的分叉。

## 执行规则

- 任务 A/B/C 可以一个 commit（中文 commit message，风格参考 `git log`）；
- 落地后本地跑 `python3 scripts/check-strings.py .`——你补出 `StringsData.swift` 之后，
  第 6 项会从「跳过」变成真校验，必须绿；
- `design/strings.json` 是**双端共享层**，本轮 mac 侧**只读不改**。发现译文有问题请在
  报告里提出、等确认后再动——两端各改各的正是这张表要消灭的问题；
- `swift test` 全绿后再 push，CI 五道关自动回归。

## 最终交付

1. 任务 A/B/C 落地并 push，CI 五道关全绿（含新增的「Strings · 文案表同源与完整性」）；
2. 任务 D 的查证结论：mac 到底有没有摘要缓存，以及你如实记录到了哪里；
3. 本文件顶部标记本轮完成，或更新为下一轮内容；
4. 汇报：完成项 / 任务 D 结论 / 与 Windows 实现的任何不一致点。**如实报**——
   对不上就说对不上，不要凑。
