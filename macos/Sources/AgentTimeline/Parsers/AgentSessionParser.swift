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
    /// 本文件的会话元信息是否已应用（codex：只认第一条 `session_meta`，见 §4.2b B1）。
    var metaApplied = false
    /// claude 专用：项目名是否已钉死（见 ClaudeParser——一场会话里 `cwd` 会被
    /// subagent/工具调用里的 `cd` 改写，只有第一次见到 cwd 时才该定项目名）。
    var projectPinned = false
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
    /// 同语义）：规整（Excerpt 档）→ 取首个非空段落（空行分隔）+ 引子续接
    /// → 上限 maxLength。折叠态 UI 仍按单行钳制显示；展开态可读到完整摘录。
    ///
    /// 永不返回空串（§3.4-1）：规整后为空（整段是围栏/表格）时回退未规整文本，
    /// 否则会把已显示的结果行抹掉——审查确认的唯一 UI 可见回归。
    static func resultExcerpt(_ text: String, maxLength: Int = DisplayLimits.resultLine) -> String {
        // A3：Kimi 的回复几乎总以 `## Summary` 起头，直接取首段会得到光秃秃的
        // 一个词（用户库里 7 条结果行字面就是 "Summary"；≤12 字符占比 kimi 38.9%
        // vs codex 4.0%）。先剥掉前导标题行，让首段落在真正的内容上。
        let normalized = TextNormalizer.normalize(dropLeadingHeadings(text), profile: .excerpt)
        var excerpt = leadInJoined(normalized, maxLength: maxLength)
        if excerpt.isEmpty {
            // 剥标题后为空 → 用含标题的原文再规整（标题总比空好）
            excerpt = leadInJoined(TextNormalizer.normalize(text, profile: .excerpt), maxLength: maxLength)
        }
        if excerpt.isEmpty {
            // 末级兜底走**未规整**原文：此时围栏/表格都还在，续接会把表格行拼进来，
            // 故这一级只取首段（§3.4-1 只要求"不为空"）。
            excerpt = firstParagraph(
                text.replacingOccurrences(of: "\r\n", with: "\n")
                    .replacingOccurrences(of: "\r", with: "\n"))
        }
        return truncate(excerpt, to: maxLength)
    }

    /// 续接上限 4 段（首段之外；含首段合计最多 5 段——判据是追加后才检查）：
    /// 真实语料里引子链最多两层，给到 4 段是防病态输入的兜底，
    /// 与 §3.4-2「凑够即停」的扫描预算同源。
    private static let leadInMaxParagraphs = 4

    /// 「引子」判据：去尾空白后以 `:` / `：` 收尾——正文在冒号之后的下一段。
    private static func isLeadIn(_ paragraph: String) -> Bool {
        let t = paragraph.trimmingCharacters(in: .whitespacesAndNewlines)
        return t.hasSuffix(":") || t.hasSuffix("：")
    }

    /// 引子续接（§3.3「引子续接」）：首段是引子时正文在下一段，继续吃到第一个
    /// 非引子段为止，段间以空格拼接。
    ///
    /// 实证：用户库 357 条结果行里 14 条冒号结尾、10 条不足 60 字，典型如
    /// `TH-0025 是一条安全类 issue,核心是一句话:`——正文在下一段的引用块里。
    ///
    /// **首段一字不动**：只对被续接进来的段落剥行首 `> ` / `- ` 标记（规整层在
    /// excerpt 档只剥全文首行），保证非引子回复的产出与本次修改前逐字节一致。
    private static func leadInJoined(_ text: String, maxLength: Int) -> String {
        var parts: [String] = []
        var length = 0
        for (index, raw) in paragraphs(text).enumerated() {
            let piece = index == 0 ? raw : TextNormalizer.stripLeadingMarkers(raw)
            parts.append(piece)
            length += piece.count + (index == 0 ? 0 : 1)   // +1 = 拼接空格
            if !isLeadIn(piece) { break }
            if parts.count > leadInMaxParagraphs || length >= maxLength { break }
        }
        return parts.joined(separator: " ")
    }

    /// 按空行切段：规整层把表格/围栏整块删掉却保留其两侧空行，故连续空行只算
    /// 一个分隔。各段已 trim，不含空段。
    private static func paragraphs(_ text: String) -> [String] {
        var result: [String] = []
        var current: [String] = []
        func flush() {
            guard !current.isEmpty else { return }
            result.append(current.joined(separator: "\n")
                .trimmingCharacters(in: .whitespacesAndNewlines))
            current = []
        }
        for line in text.replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
            .components(separatedBy: "\n") {
            if line.trimmingCharacters(in: .whitespaces).isEmpty { flush() } else { current.append(line) }
        }
        flush()
        return result.filter { !$0.isEmpty }
    }

    /// 剥掉开头连续的 markdown 标题行（`## Summary` 之类）与其后空行。
    private static func dropLeadingHeadings(_ text: String) -> String {
        var lines = text.replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
            .components(separatedBy: "\n")
        var dropped = 0
        while let first = lines.first {
            let t = first.trimmingCharacters(in: .whitespaces)
            if t.isEmpty || (t.hasPrefix("#") && t.contains(" ")) || t.allSatisfy({ $0 == "#" }) {
                lines.removeFirst(); dropped += 1
            } else {
                break
            }
        }
        // 整段都是标题/空行 → 保持原文，交给上层兜底
        return dropped == 0 || lines.isEmpty ? text : lines.joined(separator: "\n")
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
