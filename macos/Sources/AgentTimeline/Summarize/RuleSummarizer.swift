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

        let codenames = CodenameDetector.detect(in: cmd.text)
            .map { CodenameDef(name: $0, definition: "") }

        return Summary(
            title: title,
            keyPoints: Array(keyPoints),
            codenames: codenames,
            resultLine: nil,
            engine: SummaryEngineKind.rule.rawValue)
    }
}
