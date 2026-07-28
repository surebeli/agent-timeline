import Foundation

/// Kimi Code：`~/.kimi-code/sessions/wd_<项目>_<hash>/session_<uuid>/agents/main/wire.jsonl`
///
/// ⚠ 2026-07-28 换代：目录从 `~/.kimi/sessions` 迁到 `~/.kimi-code/sessions`，
/// 且 wire 协议 1.10 → 1.4 消息类型全变（旧的 TurnBegin/ContentPart 已不存在）。
/// 规范见 docs/SESSION-FORMATS.md §3，本机 44 个真实 session 实证。
///
/// 新格式的意外收获：项目目录名自带可读项目名（旧版只有不可解的 hash，
/// 只能显示 `kimi:1a2b3c4d`）。
struct KimiParser: AgentSessionParser {
    let agent = AgentKind.kimi
    let root = ParserSupport.home("~/.kimi-code/sessions")

    func watchRoots() -> [URL] { [root] }

    /// 只认**主 agent** 的 wire：`…/session_<uuid>/agents/main/wire.jsonl`。
    ///
    /// 子 agent（`agents/agent-N/wire.jsonl`）**整文件排除**——与 Claude 侧
    /// `isSidechain` 同语义（子 agent 的内部过程不是用户的时间线）。A1 实证：
    /// 子 agent 目录与 main 共用 `session_<uuid>` 目录名 → 共用 sessionId，
    /// 而它的「问」是 `origin.kind==system_trigger`（已被正确过滤）、「答」却是
    /// 普通 content.part —— 于是结果行被挂到 main 的命令节点上，代号词典也会
    /// 混入只源自子 agent 的条目。Windows 本机实测 67 个子 agent 文件 / 63 条
    /// 回复 → 5 个节点结果行被抢占。
    ///
    /// 同时锚定完整路径形状：形状不符时旧写法会把 sessionId 退化成上级目录名。
    func makeContext(for url: URL) -> ParsedFileContext? {
        guard url.lastPathComponent == "wire.jsonl",
              url.path.hasPrefix(root.path) else { return nil }
        // …/<project>/session_<uuid>/agents/main/wire.jsonl
        let agentDir = url.deletingLastPathComponent()          // main
        let agentsDir = agentDir.deletingLastPathComponent()    // agents
        let sessionDir = agentsDir.deletingLastPathComponent()  // session_<uuid>
        let projectDir = sessionDir.deletingLastPathComponent() // wd_<name>_<hash>
        guard agentDir.lastPathComponent == "main",
              agentsDir.lastPathComponent == "agents",
              sessionDir.lastPathComponent.hasPrefix("session_") else { return nil }

        return ParsedFileContext(
            url: url,
            agent: .kimi,
            sessionId: sessionDir.lastPathComponent,
            project: Self.projectName(fromWorkDir: projectDir.lastPathComponent),
            cwd: nil)
    }

    /// `wd_<name>_<12hex>` → `<name>`。项目名本身可能含下划线
    /// （`wd_hawk_agent-rs_dd8b1189a258` → `hawk_agent-rs`），所以只剥
    /// 固定的前缀与末段 hash，剥不掉就原样用目录名。
    static func projectName(fromWorkDir dir: String) -> String {
        var name = dir
        if name.hasPrefix("wd_") { name.removeFirst(3) }
        if let sep = name.lastIndex(of: "_") {
            let tail = name[name.index(after: sep)...]
            if tail.count >= 8, tail.allSatisfy({ $0.isHexDigit }) {
                name = String(name[name.startIndex..<sep])
            }
        }
        return name.isEmpty ? dir : name
    }

    func parse(line: String, context: inout ParsedFileContext) -> [SessionEvent] {
        guard let obj = ParserSupport.json(line), let type = obj["type"] as? String else { return [] }

        switch type {
        case "turn.prompt":
            // 只认用户发起的 prompt（origin.kind == "user"）。不用
            // context.append_message：那条通道混着注入上下文（实测 85 条 vs
            // 真实 prompt 39 条）。
            guard let origin = obj["origin"] as? [String: Any],
                  origin["kind"] as? String == "user",
                  let input = obj["input"] as? [[String: Any]] else { return [] }
            let text = input
                .filter { $0["type"] as? String == "text" }
                .compactMap { $0["text"] as? String }
                .joined(separator: "\n")
            guard !ParserSupport.isIgnoredContent(text), !isSlashCommand(text) else { return [] }
            let cmd = UserCommand(
                agent: .kimi,
                project: context.project,
                cwd: context.cwd,
                sessionId: context.sessionId,
                timestamp: Self.timestamp(obj["time"]),
                text: text,
                sourceFile: context.url.path)
            return [.userCommand(cmd)]

        case "context.append_loop_event":
            // 回复正文只取 part.type == "text"；"think" 是模型思考过程
            // （实测 324 条 think vs 49 条 text），不是它给出的答复。
            guard let event = obj["event"] as? [String: Any],
                  event["type"] as? String == "content.part",
                  let part = event["part"] as? [String: Any],
                  part["type"] as? String == "text",
                  let text = part["text"] as? String,
                  !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            else { return [] }
            return [.assistantText(
                agent: .kimi,
                sessionId: context.sessionId,
                timestamp: Self.timestamp(obj["time"]),
                text: text)]

        default:
            // metadata / config.update / tools.* / permission.* /
            // context.append_message / usage.record / 其余 loop 事件
            return []
        }
    }

    /// wire 1.4 的 `time` 是毫秒 epoch。
    private static func timestamp(_ raw: Any?) -> Date {
        guard let ms = (raw as? NSNumber)?.doubleValue, ms > 0 else { return Date() }
        return Date(timeIntervalSince1970: ms / 1000)
    }

    /// 裸斜杠命令（`/model`）是 UI 动作不是 prompt；**带参数的**（`/compact 全部`）
    /// 承载真实用户意图要保留——口径与 win KimiParser 一致（含空格即保留）。
    private func isSlashCommand(_ text: String) -> Bool {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return t.hasPrefix("/") && !t.contains(" ") && !t.contains("\n") && t.count <= 24
    }
}
