import Foundation

/// zcode（Z Code CLI）：`~/.zcode/cli/agents/sess_<uuid>/agent_<uuid>/transcript.jsonl`
///
/// 规范见 docs/SESSION-FORMATS.md §4，与 Windows `Core/Parsers/ZcodeParser.cs` 同源。
///
/// 语义注：agents 目录记录的是**任务派发**（含子 agent），不是主会话人机对话——
/// 时间线粒度为「一次任务 = 一个节点」。
struct ZcodeParser: AgentSessionParser {
    let agent = AgentKind.zcode

    /// 内建根目录（与 win 一致）。路径是产品事实不是用户偏好，故不做成可配项。
    static let defaultRoot = "~/.zcode/cli/agents"

    func watchRoots() -> [URL] { [ParserSupport.home(Self.defaultRoot)] }

    func makeContext(for url: URL) -> ParsedFileContext? {
        guard url.lastPathComponent == "transcript.jsonl",
              url.path.hasPrefix(ParserSupport.home(Self.defaultRoot).path) else { return nil }
        let agentDir = url.deletingLastPathComponent()      // agent_<uuid>
        let sessDir = agentDir.deletingLastPathComponent()  // sess_<uuid>

        // 项目名取同目录 sidecar metadata.json 的 cwd 末段；没有 sidecar 就回退
        // sess_ 目录名前 13 字符（"sess_" + 8 位 uuid，与 win 同口径）。
        let sessName = sessDir.lastPathComponent
        let fallback = sessName.count > 13 ? String(sessName.prefix(13)) : sessName
        var project = fallback
        let sidecar = agentDir.appendingPathComponent("metadata.json")
        if let data = try? Data(contentsOf: sidecar),
           let meta = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
           let cwd = meta["cwd"] as? String {
            project = ParserSupport.projectName(fromCwd: cwd, fallback: fallback)
        }

        return ParsedFileContext(
            url: url,
            agent: .zcode,
            sessionId: agentDir.lastPathComponent,   // agent_<uuid>：一次派发一个会话
            project: project,
            cwd: nil)
    }

    func parse(line: String, context: inout ParsedFileContext) -> [SessionEvent] {
        guard let obj = ParserSupport.json(line),
              let type = obj["type"] as? String,
              type == "turn_started" || type == "turn_complete",
              let payload = obj["payload"] as? [String: Any] else { return [] }
        // 过程事件（model_streaming / tool_call_scheduled / … ）全部落在上面的类型
        // 过滤里，不必逐个枚举。
        guard let ts = ParserSupport.timestamp(obj["timestamp"], carriedBy: &context) else { return [] }

        if type == "turn_started" {
            guard let input = (payload["input"] as? String)?
                .trimmingCharacters(in: .whitespacesAndNewlines), !input.isEmpty else { return [] }
            let cmd = UserCommand(
                agent: .zcode,
                project: context.project,
                cwd: context.cwd,
                sessionId: context.sessionId,
                timestamp: ts,
                text: input,
                sourceFile: context.url.path)
            return [.userCommand(cmd)]
        }

        // turn_complete：结果行由 AppDelegate 过 resultExcerpt（规整→首段→≤500），
        // 代号挖掘吃的是这里发出的**未截断全文**——与 win 的 ResultLine/FullText
        // 拆分等价，只是 mac 把规整放在解析器出口之外。
        guard let response = (payload["response"] as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines), !response.isEmpty else { return [] }
        return [.assistantText(
            agent: .zcode,
            sessionId: context.sessionId,
            timestamp: ts,
            text: response)]
    }
}
