import Foundation

/// ~/.claude/projects/<project-slug>/<session-uuid>.jsonl
/// One JSON object per line; user prompts are `type=="user"` lines, filtered per
/// docs/SESSION-FORMATS.md.
struct ClaudeParser: AgentSessionParser {
    let agent = AgentKind.claude
    let root = ParserSupport.home("~/.claude/projects")

    func watchRoots() -> [URL] { [root] }

    func makeContext(for url: URL) -> ParsedFileContext? {
        guard url.pathExtension == "jsonl",
              url.path.hasPrefix(root.path) else { return nil }
        let sessionId = url.deletingPathExtension().lastPathComponent
        // Project display name resolves from the per-line cwd; slug as placeholder.
        let slug = url.deletingLastPathComponent().lastPathComponent
        return ParsedFileContext(url: url, agent: .claude, sessionId: sessionId, project: slug, cwd: nil)
    }

    func parse(line: String, context: inout ParsedFileContext) -> [SessionEvent] {
        guard let obj = ParserSupport.json(line), let type = obj["type"] as? String else { return [] }

        if let cwd = obj["cwd"] as? String, context.cwd != cwd {
            context.cwd = cwd
            context.project = (cwd as NSString).lastPathComponent
            // Never surface our own summarizer's headless sessions.
            if cwd == AppSettings.summarizerScratchDir {
                context.disabled = true
            }
        }
        guard !context.disabled else { return [] }

        switch type {
        case "user":
            if obj["isMeta"] as? Bool == true { return [] }
            if obj["isSidechain"] as? Bool == true { return [] }
            guard let message = obj["message"] as? [String: Any],
                  let raw = extractText(message["content"]),
                  !ParserSupport.isIgnoredContent(raw),
                  let ts = ParserSupport.parseISO(obj["timestamp"] as? String)
            else { return [] }
            // A slash command reaches us only as an echo block; convert it to
            // "/name args" instead of dropping the user's command (P0).
            var text = raw
            if let converted = ParserSupport.convertCommandEcho(raw) {
                guard !converted.isEmpty else { return [] }
                text = converted
            }
            let cmd = UserCommand(
                agent: .claude,
                project: context.project,
                cwd: context.cwd,
                sessionId: (obj["sessionId"] as? String) ?? context.sessionId,
                timestamp: ts,
                text: text,
                sourceFile: context.url.path)
            return [.userCommand(cmd)]

        case "assistant":
            guard let message = obj["message"] as? [String: Any],
                  let text = extractText(message["content"]),
                  !text.isEmpty,
                  let ts = ParserSupport.parseISO(obj["timestamp"] as? String)
            else { return [] }
            return [.assistantText(
                agent: .claude,
                sessionId: (obj["sessionId"] as? String) ?? context.sessionId,
                timestamp: ts,
                text: text)]

        case "attachment":
            // Prompts typed while a turn is running may be consumed mid-turn and
            // never replayed as a type=user line; the queued_command attachment is
            // then the only record. (Dequeued prompts DO replay as user lines and
            // carry no such attachment, so this path is duplicate-free.)
            guard obj["isSidechain"] as? Bool != true,
                  let att = obj["attachment"] as? [String: Any],
                  att["type"] as? String == "queued_command",
                  let text = att["prompt"] as? String,
                  !ParserSupport.isIgnoredContent(text),
                  let ts = ParserSupport.parseISO(obj["timestamp"] as? String)
            else { return [] }
            let queued = UserCommand(
                agent: .claude,
                project: context.project,
                cwd: context.cwd,
                sessionId: (obj["sessionId"] as? String) ?? context.sessionId,
                timestamp: ts,
                text: text,
                sourceFile: context.url.path)
            return [.userCommand(queued)]

        default:
            return []
        }
    }

    /// message.content is either a plain string or an array of segments;
    /// only `text` segments count (tool_result segments are tool output, not user input).
    private func extractText(_ content: Any?) -> String? {
        if let s = content as? String { return s }
        guard let parts = content as? [[String: Any]] else { return nil }
        let texts = parts.compactMap { part -> String? in
            guard part["type"] as? String == "text" else { return nil }
            return part["text"] as? String
        }
        guard !texts.isEmpty else { return nil }
        return texts.joined(separator: "\n")
    }
}
