import Foundation

/// Per-file mutable parse state. Created once per tracked file and kept across
/// incremental reads so metadata found early (e.g. codex session_meta) applies
/// to later lines.
struct ParsedFileContext {
    let url: URL
    let agent: AgentKind
    var sessionId: String
    var project: String
    var cwd: String?
    /// Set when the file turns out to be one we must ignore entirely
    /// (e.g. our own summarizer's headless sessions).
    var disabled = false
}

protocol AgentSessionParser: Sendable {
    var agent: AgentKind { get }
    /// Directory roots this parser wants watched.
    func watchRoots() -> [URL]
    /// nil if the file does not belong to this parser.
    func makeContext(for url: URL) -> ParsedFileContext?
    func parse(line: String, context: inout ParsedFileContext) -> [SessionEvent]
}

enum ParserSupport {
    static let isoFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    static let isoFormatterNoFraction: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    static func parseISO(_ string: String?) -> Date? {
        guard let string else { return nil }
        return isoFormatter.date(from: string) ?? isoFormatterNoFraction.date(from: string)
    }

    static func json(_ line: String) -> [String: Any]? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, let data = trimmed.data(using: .utf8) else { return nil }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    /// Injected/meta content that must never surface as a user command.
    /// NOTE: slash command echo blocks (`<command-name>` / `<command-message>`)
    /// are deliberately NOT here — they carry a real user command and are
    /// converted, not dropped (see `convertCommandEcho`).
    static let ignoredPrefixes = [
        "<local-command-caveat", "<local-command-stdout",
        "<system-reminder", "<user_instructions", "<environment_context", "<task-notification",
        "Caveat:", "[Request interrupted",
        "This session is being continued from",  // post-compaction continuation blob
    ]

    static func isIgnoredContent(_ text: String) -> Bool {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if t.isEmpty { return true }
        return ignoredPrefixes.contains { t.hasPrefix($0) }
    }

    private static let commandNameRegex = try! NSRegularExpression(
        pattern: #"<command-name>\s*(/[^<\s]+)\s*</command-name>"#)
    private static let commandArgsRegex = try! NSRegularExpression(
        pattern: #"<command-args>\s*(.*?)\s*</command-args>"#,
        options: [.dotMatchesLineSeparators])

    /// Slash command echo blocks are the ONLY record of a `/foo bar` prompt —
    /// dropping them loses a user command outright (docs/TEXT-NORMALIZATION.md
    /// §2, P0). Two field orders exist in the corpus (`<command-name>` first and
    /// `<command-message>` first), so match on either opener and pull the name
    /// out by regex; a non-empty `<command-args>` is the user's own typing.
    ///
    /// - Returns: `nil` when `text` is not a command echo (caller keeps it as-is),
    ///   `.some(nil)` semantics are avoided by signalling "echo but unusable"
    ///   through an empty string, which the caller drops.
    static func convertCommandEcho(_ text: String) -> String? {
        guard text.hasPrefix("<command-name>") || text.hasPrefix("<command-message>") else {
            return nil
        }
        let range = NSRange(text.startIndex..., in: text)
        guard let nameMatch = commandNameRegex.firstMatch(in: text, range: range),
              let nameRange = Range(nameMatch.range(at: 1), in: text) else {
            return ""  // echo block without a usable name → drop
        }
        let name = String(text[nameRange])
        if let argsMatch = commandArgsRegex.firstMatch(in: text, range: range),
           let argsRange = Range(argsMatch.range(at: 1), in: text) {
            let args = String(text[argsRange]).trimmingCharacters(in: .whitespacesAndNewlines)
            if !args.isEmpty { return "\(name) \(args)" }
        }
        return name
    }

    static func truncate(_ text: String, to limit: Int) -> String {
        if text.count <= limit { return text }
        return String(text.prefix(limit)) + "…"
    }

    static func home(_ path: String) -> URL {
        URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
    }
}
