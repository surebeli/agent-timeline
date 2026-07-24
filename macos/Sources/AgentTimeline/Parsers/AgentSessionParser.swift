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
    static let ignoredPrefixes = [
        "<local-command-caveat", "<local-command-stdout", "<command-name", "<command-message",
        "<system-reminder", "<user_instructions", "<environment_context", "<task-notification",
        "Caveat:", "[Request interrupted",
        "This session is being continued from",  // post-compaction continuation blob
    ]

    static func isIgnoredContent(_ text: String) -> Bool {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if t.isEmpty { return true }
        return ignoredPrefixes.contains { t.hasPrefix($0) }
    }

    static func truncate(_ text: String, to limit: Int) -> String {
        if text.count <= limit { return text }
        return String(text.prefix(limit)) + "…"
    }

    static func home(_ path: String) -> URL {
        URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
    }
}
