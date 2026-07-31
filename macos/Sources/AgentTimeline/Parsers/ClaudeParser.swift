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
        let slug = url.deletingLastPathComponent().lastPathComponent
        var context = ParsedFileContext(url: url, agent: .claude, sessionId: sessionId, project: slug, cwd: nil)
        // Pin the project to the session's *true* starting cwd up front by scanning
        // the file head, so a resumed tail (offset saved from a previous run, which
        // starts reading wherever cwd had drifted to by then — see SessionWatcher)
        // doesn't pin the project to "wherever the process happened to be at
        // restart" instead of where the session actually began.
        if let cwd = Self.firstCwd(in: url) {
            context.cwd = cwd
            context.project = ParserSupport.projectName(fromCwd: cwd, fallback: slug)
            context.projectPinned = true
            // Never surface our own summarizer's headless sessions. Must check here
            // too, not just in parse()'s per-line update below: that check only fires
            // on `context.cwd != cwd` (a *change*), but head-scan just pre-filled
            // context.cwd with this same value — so for the common case (a summarizer
            // session whose cwd is the scratch dir from line 1 and never changes),
            // parse() would never see a "change" and would never disable it. Confirmed
            // this was a real, active regression on real data before this fix landed:
            // summarizer prompts leaking into the visible timeline as project
            // "summarizer" (self-talk like "你是一个命令摘要器…"), growing every few
            // seconds while the buggy build ran.
            if cwd == AppSettings.summarizerScratchDir {
                context.disabled = true
            }
        }
        return context
    }

    /// Scans the file head (capped, not the whole file — sessions run tens of MB)
    /// for the first line carrying a `cwd`, i.e. the session's starting directory.
    static func firstCwd(in url: URL, capBytes: Int = 256 * 1024) -> String? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let data = try? handle.read(upToCount: capBytes), !data.isEmpty,
              let text = String(data: data, encoding: .utf8) else { return nil }
        for line in text.split(separator: "\n", omittingEmptySubsequences: true) {
            if let cwd = ParserSupport.json(String(line))?["cwd"] as? String { return cwd }
        }
        return nil
    }

    func parse(line: String, context: inout ParsedFileContext) -> [SessionEvent] {
        guard let obj = ParserSupport.json(line), let type = obj["type"] as? String else { return [] }

        if let cwd = obj["cwd"] as? String, context.cwd != cwd {
            context.cwd = cwd
            // 项目名只钉一次：一场会话里的 cwd 会被 subagent、工具调用里的 cd 改写
            // （实机会话见过一场对话摊成 7 个不同 cwd），只有第一次见到 cwd 时才该
            // 定项目名，后续漂移只用于下面的"是不是摘要器自己的会话"判定。
            // 正常情况下 makeContext 里的头部扫描已经钉过了；这里是它没扫到时的兜底
            // （比如文件头被截断），退而求其次钉在"解析过程里第一次见到的 cwd"上。
            if !context.projectPinned {
                context.project = ParserSupport.projectName(fromCwd: cwd, fallback: context.project)
                context.projectPinned = true
            }
            // Never surface our own summarizer's headless sessions.
            if cwd == AppSettings.summarizerScratchDir {
                context.disabled = true
            }
        }
        guard !context.disabled else { return [] }
        // 回退基准由**任意**带可解析时间戳的行喂养（含 system / file-history-snapshot
        // 等非事件行）——与 win 同口径：越近的锚点越能把缺时间戳的行放回真实邻居旁。
        let lineTimestamp = ParserSupport.timestamp(obj["timestamp"], carriedBy: &context)

        switch type {
        case "user":
            if obj["isMeta"] as? Bool == true { return [] }
            if obj["isSidechain"] as? Bool == true { return [] }
            guard let message = obj["message"] as? [String: Any],
                  let rawText = extractText(message["content"]),
                  // 判定基准与 win 一致：先 trim 再做忽略前缀 / 回显块判定，
                  // 否则带前导空白的回显块会整块 XML 泄漏成节点正文。
                  case let raw = rawText.trimmingCharacters(in: .whitespacesAndNewlines),
                  !ParserSupport.isIgnoredContent(raw),
                  let ts = lineTimestamp
            else { return [] }
            // A slash command reaches us only as an echo block; convert it to
            // "/name args" instead of dropping the user's command (P0).
            var text = raw
            if let converted = ParserSupport.convertCommandEcho(raw)
                ?? ParserSupport.convertBashInput(raw) {
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
            // 子 agent 输出不是本会话的结果（win ClaudeParser.cs 同守卫）：
            // subagents/*.jsonl 里的 assistant 行写的是父会话 id，不挡会把子 agent
            // 的话当成父节点的结果行，并污染代号挖掘语料。
            if obj["isSidechain"] as? Bool == true { return [] }
            guard let message = obj["message"] as? [String: Any],
                  let text = extractText(message["content"]),
                  !text.isEmpty,
                  let ts = lineTimestamp
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
                  let rawPrompt = att["prompt"] as? String,
                  case let text = rawPrompt.trimmingCharacters(in: .whitespacesAndNewlines),
                  !ParserSupport.isIgnoredContent(text),
                  let ts = lineTimestamp
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
