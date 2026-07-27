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
/// **当前双端差异（待一次性拍板，§5.1 P4 未决项）**：
///
/// | 用途 | mac（本表） | win（`ParserUtil` 调用点） |
/// |---|---|---|
/// | 规则摘要标题 | 40 | 20 |
/// | 规则摘要要点 | 60 × 5 条 | 30 × 3 条 |
/// | LLM 摘要标题 | 60 | 40 |
/// | LLM 摘要要点 | 80 × 6 条 | 60 × 5 条 |
/// | LLM 代号定义 | 60 | 60 ✅ |
/// | 结果行 | 500 | 500 ✅ |
/// | LLM prompt 输入 | 4000 | 4000 ✅ |
///
/// mac 侧数值保持现状未动：面板宽度与字号两端不同（mac 340pt/13.5pt 台账排版
/// 经实机验证），把 mac 压到 win 的 20/30 会让标题被截成半句。真要统一，应先
/// 定"以哪端的排版为准"再一次性改双端，而不是在移植 PR 里单方面动 UI。
enum DisplayLimits {
    /// 规则摘要（LLM 结果落库前的即时展示）。
    enum Rule {
        static let title = 40
        static let keyPoint = 60
        static let keyPointCount = 5
    }

    /// LLM 摘要落库值。
    enum LLM {
        static let title = 60
        static let keyPoint = 80
        static let keyPointCount = 6
        static let codenameDefinition = 60
        /// prompt 输入上限（双端一致）。
        static let promptInput = 4000
    }

    /// 结果行：规整 → 首段 → 此上限（双端一致，docs §3.1 Excerpt 档）。
    static let resultLine = 500
}
