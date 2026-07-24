import Foundation

/// ~/.codex/sessions/YYYY/MM/DD/rollout-<ts>-<uuid>.jsonl
/// Lines are {timestamp, type, payload}; user prompts are
/// type=="event_msg" && payload.type=="user_message".
struct CodexParser: AgentSessionParser {
    let agent = AgentKind.codex
    let root = ParserSupport.home("~/.codex/sessions")

    func watchRoots() -> [URL] { [root] }

    func makeContext(for url: URL) -> ParsedFileContext? {
        guard url.pathExtension == "jsonl",
              url.lastPathComponent.hasPrefix("rollout-"),
              url.path.hasPrefix(root.path) else { return nil }
        // rollout-2026-04-23T11-21-47-<uuid>.jsonl → trailing uuid as fallback id
        let stem = url.deletingPathExtension().lastPathComponent
        let sessionId = stem.split(separator: "-").suffix(5).joined(separator: "-")
        var context = ParsedFileContext(url: url, agent: .codex, sessionId: sessionId, project: "codex", cwd: nil)
        // session_meta is the first line; when resuming past a persisted offset
        // (app restart mid-session) we'd otherwise lose cwd/project and the
        // summarizer-scratch exclusion. Re-seed from the file head.
        if let handle = try? FileHandle(forReadingFrom: url) {
            if let head = try? handle.read(upToCount: 16384),
               let nl = head.firstIndex(of: 0x0A),
               let line = String(data: head[head.startIndex..<nl], encoding: .utf8) {
                _ = parse(line: line, context: &context)
            }
            try? handle.close()
        }
        return context
    }

    func parse(line: String, context: inout ParsedFileContext) -> [SessionEvent] {
        guard let obj = ParserSupport.json(line), let type = obj["type"] as? String else { return [] }
        let payload = obj["payload"] as? [String: Any] ?? [:]

        switch type {
        case "session_meta":
            if let id = payload["id"] as? String { context.sessionId = id }
            if let cwd = payload["cwd"] as? String {
                context.cwd = cwd
                context.project = (cwd as NSString).lastPathComponent
                if cwd == AppSettings.summarizerScratchDir {
                    context.disabled = true
                }
            }
            return []

        case "event_msg":
            guard !context.disabled else { return [] }
            let payloadType = payload["type"] as? String
            guard let ts = ParserSupport.parseISO(obj["timestamp"] as? String) else { return [] }

            if payloadType == "user_message" {
                guard let text = payload["message"] as? String,
                      !ParserSupport.isIgnoredContent(text) else { return [] }
                let cmd = UserCommand(
                    agent: .codex,
                    project: context.project,
                    cwd: context.cwd,
                    sessionId: context.sessionId,
                    timestamp: ts,
                    text: text,
                    sourceFile: context.url.path)
                return [.userCommand(cmd)]
            }
            if payloadType == "task_complete" {
                guard let last = payload["last_agent_message"] as? String, !last.isEmpty else { return [] }
                return [.assistantText(agent: .codex, sessionId: context.sessionId, timestamp: ts, text: last)]
            }
            return []

        default:
            return []
        }
    }
}
