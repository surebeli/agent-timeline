import Foundation

/// 展示态截断常量的单一出处（docs/TEXT-NORMALIZATION.md §4-5、§5.1 P4）。
///
/// 此前这些数字散落在 RuleSummarizer / SummaryPrompt / AppDelegate 各处，改一个
/// 值要翻三个文件；更麻烦的是双端各写各的，谁也不知道差在哪。集中到这里后，
/// 双端对齐只需改这一张表。
///
/// **计量口径**：mac 一律按 `Character`（grapheme cluster）计数
/// （`ParserSupport.truncate`），win 按 UTF-16 code unit（`ParserUtil.Clip`）。
/// 对纯 ASCII / CJK 无差异；差异只出现在代理对与组合序列上（emoji、旗帜、
/// ZWJ 家族），两端都保证不劈开簇，故只影响"能装多少个 emoji"，不影响正确性。
///
/// **设计原则：存储只留护栏，显示层负责钳制**（PRD L3 视觉钳制）。
///
/// 这里的数字**不是排版决策**，而是防御性上限——防止畸形输入把一行撑成一兆，
/// 正常内容永远不该碰到它们。"一屏显示多少"完全交给显示层的 `lineLimit`：
/// 折叠态一行、展开态全展开、hover tooltip 兜全文。因此双端可以用同一张表，
/// 而两端 UI 各按自己的排版钳制，互不牵制。
///
/// 定档依据（本机 431 节点实测，2026-07-27）：标题长度 p50=14 / p90=25 / max=41，
/// 旧的 40 上限只在 0.7% 的节点上触发、60 的 LLM 上限一次都没触发。护栏取 120
/// ≈ max 的 3 倍，正常内容不可能触碰；压到 win 旧值 20 反而会咬掉约 15% 的标题。
///
/// **双端已统一**（win 侧 `Core/DisplayLimits.cs` 同表）：
///
/// | 用途 | 值 | 备注 |
/// |---|---|---|
/// | 摘要标题（规则/LLM） | 120 | 原 mac 40/60、win 20/40 |
/// | 摘要要点 | 200 × 6 条 | 原 mac 60×5/80×6、win 30×3/60×5 |
/// | LLM 代号定义 | 120 | 原双端 60 |
/// | 结果行 | 500 | 双端原已一致 |
/// | LLM prompt 输入 | 4000 | 双端原已一致 |
///
/// **计量口径**：mac 按 `Character`（grapheme cluster）计数，win 按 UTF-16
/// code unit。对纯 ASCII / CJK 无差异，差异只出现在代理对与组合序列上，且两端
/// 都保证不劈开簇——护栏水位下这点差异永远不会被触发。
///
/// 旧数据不迁移（同 docs §5.2-2）：已存库的截断值保持原样，新数据走新护栏。
enum DisplayLimits {
    /// 摘要标题：规则档与 LLM 档同值——它们渲染在同一个 UI 槽位里。
    static let summaryTitle = 120
    /// 摘要要点：单条上限与条数上限。
    static let keyPoint = 200
    static let keyPointCount = 6
    /// LLM 提取的代号定义（chip popover / 词典面板内全文展示，无行数钳制）。
    static let codenameDefinition = 120
    /// 结果行：规整 → 首段 → 此上限（docs §3.1 Excerpt 档）。
    static let resultLine = 500
    /// LLM prompt 输入上限。
    static let promptInput = 4000
}
