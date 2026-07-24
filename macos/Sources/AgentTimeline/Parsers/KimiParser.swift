import Foundation

/// ~/.kimi/sessions/<project-hash>/<session-uuid>/wire.jsonl
/// Lines are {timestamp: unixSeconds, message: {type, payload}}; user prompts
/// are TurnBegin payload.user_input text segments.
struct KimiParser: AgentSessionParser {
    let agent = AgentKind.kimi
    let root = ParserSupport.home("~/.kimi/sessions")

    func watchRoots() -> [URL] { [root] }

    func makeContext(for url: URL) -> ParsedFileContext? {
        guard url.lastPathComponent == "wire.jsonl",
              url.path.hasPrefix(root.path) else { return nil }
        let sessionDir = url.deletingLastPathComponent()
        let sessionId = sessionDir.lastPathComponent
        let projectHash = sessionDir.deletingLastPathComponent().lastPathComponent
        // No public hash→cwd mapping; prefer the session's custom title, else hash prefix.
        var project = "kimi:" + String(projectHash.prefix(8))
        let stateURL = sessionDir.appendingPathComponent("state.json")
        if let data = try? Data(contentsOf: stateURL),
           let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
           let title = obj["custom_title"] as? String, !title.isEmpty {
            project = ParserSupport.truncate(title, to: 24)
        }
        return ParsedFileContext(url: url, agent: .kimi, sessionId: sessionId, project: project, cwd: nil)
    }

    func parse(line: String, context: inout ParsedFileContext) -> [SessionEvent] {
        guard let obj = ParserSupport.json(line),
              let message = obj["message"] as? [String: Any],
              let type = message["type"] as? String else { return [] }
        let payload = message["payload"] as? [String: Any] ?? [:]
        let ts = (obj["timestamp"] as? Double).map { Date(timeIntervalSince1970: $0) } ?? Date()

        switch type {
        case "TurnBegin":
            guard let inputs = payload["user_input"] as? [[String: Any]] else { return [] }
            let text = inputs
                .filter { $0["type"] as? String == "text" }
                .compactMap { $0["text"] as? String }
                .joined(separator: "\n")
            guard !ParserSupport.isIgnoredContent(text), !isSlashCommand(text) else { return [] }
            let cmd = UserCommand(
                agent: .kimi,
                project: context.project,
                cwd: context.cwd,
                sessionId: context.sessionId,
                timestamp: ts,
                text: text,
                sourceFile: context.url.path)
            return [.userCommand(cmd)]

        case "TurnEnd":
            // Not always present; when it is, surface any final text as the result line.
            if let text = payload["text"] as? String, !text.isEmpty {
                return [.assistantText(agent: .kimi, sessionId: context.sessionId, timestamp: ts, text: text)]
            }
            return []

        default:
            return []
        }
    }

    /// Bare slash commands like "/model" are UI actions, not prompts.
    private func isSlashCommand(_ text: String) -> Bool {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return t.hasPrefix("/") && !t.contains("\n") && t.count < 40
    }
}
