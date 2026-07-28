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
    /// 本文件最近一次成功解析出的时间戳，用于给缺失/畸形时间戳的行顺延
    /// （见 `ParserSupport.timestamp(_:carriedBy:)`）。
    var lastTimestamp: Date?
    /// 当前轮次里最后一条 agent 消息，等轮次结束事件到达时落为结果行。
    /// Grok 用（`agent_message_chunk` 一轮多条，只有末条是答复，见 GrokParser）。
    var pendingAssistantText: String?
    var pendingAssistantTimestamp: Date?
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

    /// 宽松形态解析：严格 ISO8601 之外，再认无时区 / 空格分隔 / 纯日期几种
    /// 常见写法（.NET 的 DateTimeOffset.TryParse 本来就吃这些，双端对齐）。
    private static let lenientFormatters: [DateFormatter] = [
        "yyyy-MM-dd'T'HH:mm:ss.SSS", "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm:ssZZZZZ", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd",
    ].map {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(secondsFromGMT: 0)
        f.dateFormat = $0
        return f
    }

    static func parseISO(_ string: String?) -> Date? {
        guard let string else { return nil }
        if let d = isoFormatter.date(from: string) ?? isoFormatterNoFraction.date(from: string) {
            return d
        }
        return lenientFormatters.lazy.compactMap { $0.date(from: string) }.first
    }

    /// 双端共同规则（docs/TEXT-NORMALIZATION.md §4.2-14）：
    /// 形态放宽 → 解析不出则**顺延本文件上一条成功的时间戳**（确定性、与真实
    /// 邻居相邻、重扫结果稳定）→ 文件里还没有任何成功时间戳才丢弃该行。
    /// 不用「回退当前时间」：那会让节点跳到时间线顶部，且 ts 参与唯一键，
    /// 文件重建后重扫会插出重复行。
    static func timestamp(_ raw: Any?, carriedBy context: inout ParsedFileContext) -> Date? {
        if let parsed = parseISO(raw as? String) {
            context.lastTimestamp = parsed
            return parsed
        }
        return context.lastTimestamp
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
        // `!cmd` 直通 shell 的输出侧不是人说的话；输入侧由 convertBashInput 保留。
        "<bash-stdout", "<bash-stderr",
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

    private static let bashInputRegex = try! NSRegularExpression(
        pattern: #"^<bash-input>([\s\S]*?)</bash-input>"#)

    /// `!git pull` 直通 shell：命令本身是用户真实操作（与 slash 命令同理），
    /// 转成 `$ cmd` 保留；输出侧 `<bash-stdout>`/`<bash-stderr>` 已在
    /// `ignoredPrefixes` 里剥掉。语义与 win `ClaudeParser.BashInputRegex` 一致。
    ///
    /// - Returns: `nil` 表示不是直通块（调用方原样保留）；空串表示是直通块但
    ///   取不到命令（调用方丢弃）。
    static func convertBashInput(_ text: String) -> String? {
        guard text.hasPrefix("<bash-input>") else { return nil }
        guard let m = bashInputRegex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
              let r = Range(m.range(at: 1), in: text) else { return "" }
        let cmd = String(text[r]).trimmingCharacters(in: .whitespacesAndNewlines)
        return cmd.isEmpty ? "" : "$ \(cmd)"
    }

    static func truncate(_ text: String, to limit: Int) -> String {
        if text.count <= limit { return text }
        return String(text.prefix(limit)) + "…"
    }

    /// 结果摘录（docs/TEXT-NORMALIZATION.md §3，与 win `ParserUtil.ResultExcerpt`
    /// 同语义）：规整（Excerpt 档）→ 取首个非空段落（空行分隔）→ 上限 maxLength。
    /// 折叠态 UI 仍按单行钳制显示；展开态用户可读到完整首段。
    ///
    /// 永不返回空串（§3.4-1）：规整后为空（整段是围栏/表格）时回退未规整文本，
    /// 否则会把已显示的结果行抹掉——审查确认的唯一 UI 可见回归。
    static func resultExcerpt(_ text: String, maxLength: Int = DisplayLimits.resultLine) -> String {
        let normalized = TextNormalizer.normalize(text, profile: .excerpt)
        var excerpt = firstParagraph(normalized)
        if excerpt.isEmpty {
            excerpt = firstParagraph(
                text.replacingOccurrences(of: "\r\n", with: "\n")
                    .replacingOccurrences(of: "\r", with: "\n"))
        }
        return truncate(excerpt, to: maxLength)
    }

    private static func firstParagraph(_ text: String) -> String {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let end = t.range(of: "\n\n") else { return t }
        return String(t[t.startIndex..<end.lowerBound])
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// cwd 末段作项目名（镜像 win `ParserUtil.ProjectNameFromCwd`）：
    /// `\` 归一为 `/`、去尾斜杠、取末段，空白则回退。
    static func projectName(fromCwd cwd: String?, fallback: String) -> String {
        guard let cwd else { return fallback }
        var normalized = cwd.replacingOccurrences(of: "\\", with: "/")
        while normalized.hasSuffix("/") { normalized.removeLast() }
        let leaf = normalized.split(separator: "/").last.map(String.init) ?? ""
        return leaf.trimmingCharacters(in: .whitespaces).isEmpty ? fallback : leaf
    }

    static func home(_ path: String) -> URL {
        URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
    }
}
