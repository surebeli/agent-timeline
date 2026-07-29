import Foundation

/// Zero-dependency fallback: first line as title, leading bullet-ish lines as
/// key points, regex codenames. Always succeeds, runs synchronously.
struct RuleSummarizer: Sendable {
    func summarize(_ cmd: UserCommand) -> Summary {
        // 展示态才规整（Summary 档：围栏只保护不删、行首列表/引用前缀全剥）；
        // 命令原文 cmd.text 永不改写，代号挖掘另走未规整全文。
        // 旧的 bulletPrefixes 筛选已删除——它剥不掉 "1. " 又会吃掉 "--force"，
        // 与 §3.3 的列表前缀规则冲突（win 端同步删除了 StripMarkdownNoise）。
        let display = TextNormalizer.normalize(cmd.text, profile: .summary)
        let lines = (display.isEmpty ? cmd.text : display)
            .split(separator: "\n", omittingEmptySubsequences: true)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }

        let title = ParserSupport.truncate(lines.first ?? "（空命令）", to: DisplayLimits.summaryTitle)

        let keyPoints = lines.dropFirst()
            .prefix(DisplayLimits.keyPointCount)
            .map { ParserSupport.truncate($0, to: DisplayLimits.keyPoint) }

        var codenames = CodenameDetector.detectDefinitions(in: cmd.text)
            .map { CodenameDef(name: $0.name, definition: $0.definition, status: CodenameStatus.defined.rawValue) }
        let definedNames = Set(codenames.map(\.name))
        codenames += CodenameDetector.detect(in: cmd.text)
            .filter { !definedNames.contains($0) }
            .map { CodenameDef(name: $0, definition: "") }

        return Summary(
            title: title,
            keyPoints: Array(keyPoints),
            codenames: codenames,
            resultLine: nil,
            engine: SummaryEngineKind.rule.rawValue,
            kind: guessKind(cmd.text))
    }

    /// 类型识别词表（四语常开，docs/TEXT-NORMALIZATION.md §3.6）。
    ///
    /// 这些是**识别**词、不是展示文案，所以不进 design/strings.json：会话里出现哪种语言
    /// 与界面语言无关，四张表必须同时生效。与 win `RuleSummarizer.KindRules` 逐条同表。
    ///
    /// 拉丁词全部走**词边界**匹配，否则 `prefix`/`suffix` 会把一切命令判成 fix。
    ///
    /// ⚠️ **中日同形词是这张表最大的坑**：日语词若与简体中文写法完全相同，就等于同时
    /// 加进了中文表。5189 条真实命令上量过，以下几个必须**排除在外**——
    /// · `要求`（本想给 requirement）：中文里是高频通用动词（"按要求执行"），
    ///   而 requirement 判在 task 之前，实测把 31 条任务误判成需求；
    /// · `判断`（本想给 decision）：同理，中文"判断一下"随处可见。
    /// 日语侧改用无同形碰撞的 `要件/仕様`、`決定/選定/方針`，覆盖不受影响。
    /// 反过来 `調査/検討/説明/実装/対応` 用的是日本新字体，简体中文写法不同，安全。
    private static let kindRules: [(NodeKind, [String])] = [
        (.fix, [
            "修复", "报错", "崩溃", "闪退",                                  // zh
            "fix", "bug", "debug", "crash", "regression",                   // en
            "修正", "不具合", "バグ", "エラー", "クラッシュ", "障害",          // ja
            "수정", "버그", "오류", "에러", "크래시", "장애",                 // ko
        ]),
        (.research, [
            "调研", "研究", "对比", "评估", "分析一下",                       // zh
            "survey", "research", "investigate", "benchmark",               // en
            "調査", "検討", "比較", "評価", "リサーチ",                       // ja
            "조사", "검토", "비교", "평가", "리서치",                         // ko
        ]),
        (.learning, [
            "学习", "讲解", "解释", "什么是", "怎么理解", "教我",              // zh
            "explain", "tutorial", "what is", "how does",                   // en
            "説明", "解説", "教えて", "とは何", "学習",                       // ja
            "설명", "알려줘", "무엇인가", "배우",                             // ko
        ]),
        (.requirement, [
            "需求", "功能描述", "产品述求",                                   // zh
            "prd", "requirement", "spec", "specification", "user story",    // en
            "要件", "仕様",                                                  // ja
            "요구사항", "요건", "사양", "스펙",                               // ko
        ]),
        (.decision, [
            "决策", "选型", "定方案", "拍板", "确认方案",                      // zh
            "decision", "decide", "tradeoff", "trade-off",                  // en
            "決定", "選定", "方針",                                          // ja
            "결정", "선정", "판단", "방침",                                  // ko
        ]),
        (.task, [
            "任务", "实现", "开发", "执行", "完成", "部署", "重构",            // zh
            "implement", "deploy", "refactor",                              // en
            "実装", "開発", "デプロイ", "リファクタ", "対応", "タスク",         // ja
            "구현", "개발", "배포", "리팩터", "작업", "태스크",                // ko
        ]),
    ]

    /// Keyword fallback until the LLM summary lands（PRD §3.3b 规则引擎兜底）。
    private func guessKind(_ text: String) -> String? {
        let t = TextNormalizer.forMatch(text)
        for (kind, keywords) in Self.kindRules
        where keywords.contains(where: { TextNormalizer.containsKeyword(t, TextNormalizer.forMatch($0)) }) {
            return kind.rawValue
        }
        return nil
    }
}
