import Foundation

/// Zero-dependency fallback: first line as title, leading bullet-ish lines as
/// key points, regex codenames. Always succeeds, runs synchronously.
struct RuleSummarizer: Sendable {
    func summarize(_ cmd: UserCommand) -> Summary {
        let lines = cmd.text
            .split(separator: "\n", omittingEmptySubsequences: true)
            .map { $0.trimmingCharacters(in: .whitespaces) }

        let title = ParserSupport.truncate(lines.first ?? "（空命令）", to: 40)

        let bulletPrefixes = ["-", "*", "•", "1", "2", "3", "4", "5", "6", "7", "8", "9"]
        let keyPoints = lines.dropFirst()
            .filter { line in bulletPrefixes.contains(where: { line.hasPrefix($0) }) }
            .prefix(5)
            .map { ParserSupport.truncate($0, to: 60) }

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

    /// Keyword fallback until the LLM summary lands.
    private func guessKind(_ text: String) -> String? {
        let t = text.lowercased()
        let rules: [(NodeKind, [String])] = [
            (.fix, ["修复", "fix", "bug", "报错", "崩溃", "闪退"]),
            (.research, ["调研", "研究", "对比", "评估", "分析一下", "survey"]),
            (.learning, ["学习", "讲解", "解释", "什么是", "怎么理解", "教我"]),
            (.requirement, ["需求", "功能描述", "产品述求", "prd"]),
            (.decision, ["决策", "选型", "定方案", "拍板", "确认方案"]),
            (.task, ["任务", "实现", "开发", "执行", "完成", "部署", "重构"]),
        ]
        for (kind, keywords) in rules where keywords.contains(where: { t.contains($0) }) {
            return kind.rawValue
        }
        return nil
    }
}
