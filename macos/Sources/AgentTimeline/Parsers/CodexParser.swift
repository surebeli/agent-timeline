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
        // ⚠ 不能只读固定一小块：codex 的 session_meta 首行常常很大（本机 260 个
        // rollout 里 169 个 >16KB，最大 44KB），读少了找不到换行就整条放弃，
        // 重启续扫后项目名会退化成 "codex"（实测 379/1874 条命令受影响）。
        // 分块读到第一个换行为止，上限兜住病态的单行巨文件。
        if let handle = try? FileHandle(forReadingFrom: url) {
            var head = Data()
            let chunk = 64 * 1024
            let cap = 1024 * 1024
            while head.count < cap {
                guard let piece = try? handle.read(upToCount: chunk), !piece.isEmpty else { break }
                head.append(piece)
                if head.firstIndex(of: 0x0A) != nil { break }
            }
            if let nl = head.firstIndex(of: 0x0A),
               let line = String(data: head[head.startIndex..<nl], encoding: .utf8) {
                _ = parse(line: line, context: &context)
            }
            try? handle.close()
        }
        return context
    }

    /// `[$plugin:skill](本机 …/SKILL.md)` 开头的技能回显：留徽标文字、剥本机
    /// 绝对路径（跨机无效且泄漏用户名）。与 win CodexParser.SkillEchoRegex 同语义。
    private static let skillEchoRegex = try! NSRegularExpression(
        pattern: #"^\[(\$[^\]\n]+)\]\([^)\n]*SKILL\.md\)"#)

    static func convertSkillEcho(_ text: String) -> String {
        let range = NSRange(text.startIndex..., in: text)
        guard let m = skillEchoRegex.firstMatch(in: text, range: range),
              let badge = Range(m.range(at: 1), in: text),
              let whole = Range(m.range, in: text) else { return text }
        return (String(text[badge]) + text[whole.upperBound...])
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func parse(line: String, context: inout ParsedFileContext) -> [SessionEvent] {
        guard let obj = ParserSupport.json(line), let type = obj["type"] as? String else { return [] }
        let payload = obj["payload"] as? [String: Any] ?? [:]
        // 同上：session_meta / response_item 的时间戳也喂养回退基准（与 win 同口径）。
        let lineTimestamp = ParserSupport.timestamp(obj["timestamp"], carriedBy: &context)

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
            guard let ts = lineTimestamp else { return [] }

            if payloadType == "user_message" {
                guard let raw = payload["message"] as? String else { return [] }
                // 与 win 一致：先 trim 再判定与落库（否则同一条命令两端正文差
                // 空白，连节点 id 都不同）。
                let text = Self.convertSkillEcho(
                    raw.trimmingCharacters(in: .whitespacesAndNewlines))
                guard !ParserSupport.isIgnoredContent(text) else { return [] }
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
