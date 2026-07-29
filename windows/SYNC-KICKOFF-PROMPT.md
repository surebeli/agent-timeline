# Windows 同步开工 Prompt（四语接线轮 · 2026-07-30）

> 用法：在 Windows 机器的仓库根目录启动 agent 会话，把下面 `---` 之间的整段粘贴为首条指令。
>
> 上一轮（v0.5.1 轮：引子续接真机验证 + 重拍一览三张图）已全部完成，本文件整体替换；
> 历史见 git log。

---

你在一台 Windows 11 机器上，当前目录是仓库 agent-timeline 根目录
（远端 github.com/surebeli/agent-timeline，先 `git pull` 到最新 main）。产品是双端桌面
挂件「Agent Timeline」，两端跑在 CI 五道关下，最新发布 **v0.6.0**。

**mac 侧的多语言地基已追齐**（`10ee15b`）：`UI/Strings.swift` 加载器 + 构建期嵌入副本 +
`AppSettings.language`，CI 第五关对 mac 那半边已从「跳过」转为真校验并通过。
两端地基现在对等，**可以开始接线了**。

## 必读（动手前读完）

1. `design/strings.json` —— 69 键 × 4 语言，**本轮仍是只读**（新增键除外，见下）；
2. `macos/Sources/AgentTimeline/UI/Strings.swift` —— mac 侧加载器，与你的 `AppStrings.cs`
   行为契约逐条对齐，可用来核对两端取词是否会出现分歧；
3. `docs/TEXT-NORMALIZATION.md` §3.3b —— 你上轮提的「段数上限 4」文字偏差已订正
   （改文档不改代码：实现合计上限是 5，双端一致，不构成分叉）。

## 任务 A：69 键接线（**双端同轮，mac 侧同步进行**）

两端把界面硬编码字面量替换成查表调用。**必须同轮做**——一端换完另一端没换，
两边界面就会在同一版本里长得不一样。

- Windows 侧用 `AppStrings.S("key")` / `AppStrings.F("key", args)`；
  mac 侧用 `Strings.s(...)` / `Strings.f(...)`，两者语义已对齐；
- **接线时不要顺手改措辞**。发现某条译文别扭，写进报告等确认——
  `design/strings.json` 是共享层，两端各改各的正是这张表要消灭的问题；
- **发现缺键**：界面上有字面量但表里没有对应键时，**先在报告里列出来**。
  补键要两端同时补（四语齐全，CI 硬校验），由先发现的一方提方案；
- 接线后**逐屏目验四种语言**：中/英/日/韩切换一遍，重点看代码构建的 UI
  （托盘菜单、chip 弹层、词典面板）有没有漏刷——那些不会自动重建，
  要靠 `AppStrings.Changed` / mac 的 `Strings.didChangeNotification` 驱动；
- 设置窗要加语言选择器（`AppSettings.Language` 已就位，存字符串 rawValue）。

## 任务 B：识别词表四语化（**双端同轮，且必须一起改**）

`CodenameRegistry` 的状态关键词、`RuleSummarizer` 的分类关键词。你上轮调研出的三条
**会让现有实现出错**的发现，是本任务的核心，逐条落地并各自补断言：

1. **否定检测的位置三语不同**——中文前置（尚未完成）、日语后置（完了して**いない**）、
   韩语两头都有（**안** 완료 / 완료하**지 않**았다）。现有「关键词前两字符」逻辑对日韩
   完全不适用；且中文否定字集 `未没不别无非` 在日语里是普通汉字（`不具合`/`非表示`），
   直接复用会**误伤**——`不具合を修正完了` 会被判成否定；
2. **`ClauseSeparators` 缺 ASCII 句点 `.`**——韩语句子 100% 用 ASCII 句点结尾，
   子句窗口永远不会在句号处切断，只会撞上长度上限，邻句状态词大量串味；
3. **`TextNormalizer` 缺 NFKC 归一化**——日语全角英数 `ＷＩＰ`、半角片假名、分离浊点
   会让子串匹配整个失效。**这条影响双端**，mac 侧对应
   `precomposedStringWithCompatibilityMapping`。

第 3 条动的是 `TextNormalizer`，属规整层：**改之前先确认现有 golden 用例不受影响**
（`docs/normalize-cases.tsv` 只增不改；若某条期望值会变，先在报告里说明并等确认）。

## 任务 C：三张一览图重拍到 200% 缩放（收尾项，随手做）

上轮拍摄机主屏 100% 缩放，产出 859×676——dip 几何与比例同 mac，但像素密度只有一半，
在 HiDPI 屏上比 mac 那三张明显偏软。你自己在报告里写了「把主屏改 200% 后跑
`shoot-readme.ps1 -Install` 即可，脚本无需改动」，本轮顺手做掉，产出应为 **1718×1352**，
与 mac 三张逐位对齐。

## 执行规则

- 任务 A/B/C 分别独立 commit（中文 commit message，风格参考 `git log`）；
- 每项完成后：`msbuild windows\AgentTimeline\AgentTimeline.csproj /restore
  /p:Configuration=Release /p:Platform=x64` + `dotnet run --project windows/CoreSmokeTest
  -c Release`（当前 354 条）全绿，**任务 B 应新增日/韩语料的断言**；
- 改 `design/strings.json`、`docs/` 等**双端共享层**时**停下来报告方案**；
- `docs/normalize-cases.tsv` golden 用例**只增不改**；
- 阶段性 push，CI 五道关自动回归。

## 最终交付

1. 任务 A/B/C 落地并 push，CI 五道关全绿；
2. 任务 A 的缺键清单（若有）与任务 B 的三条落地方式写进报告；
3. `windows/README.md` 更新记录追加本轮条目；
4. 本文件顶部标记本轮完成，或更新为下一轮内容；
5. 汇报：完成项 / 四语目验结果 / 新发现问题 / 仍不一致的点。**如实报**——
   对不上就说对不上，不要凑。

## 已知分叉（本轮不做，仅供参照）

- **§4.2 第 20 条：mac 无摘要缓存**。已查证确认——mac `Store.swift` 只建
  `nodes`/`codenames`/`file_offsets` 三张表，win 另有 `summaries`；PRD §3.4 在 mac 端
  从未实现。影响是成本与调用量、不是正确性，故你本轮给缓存键加的语言维度在 mac 无对应物。
  将来补实现时**绝不能把语言混进命令 hash**（它参与唯一键，改了重扫必出重复行）；
- §4.2c Grok 编排器派发的任务书过滤——需先定双端规范。
