import Foundation

/// Grok Build — docs/SESSION-FORMATS.md §3。
///
/// `~/.grok/sessions/<URL 编码的 cwd>/<session-uuid>/updates.jsonl`
///
/// 每行一条 ACP（Agent Client Protocol）通知：
/// `{timestamp, method:"session/update", params:{sessionId, update:{sessionUpdate, …}}}`
///
///   - `user_message_chunk`  → 用户命令（一条即一条完整消息，不需拼接）
///   - `agent_message_chunk` → 暂存；一轮内有多条（工具调用之间的进度旁白）
///   - `turn_completed`      → 把**最后一条**暂存的 agent 消息落为结果行
///
/// 项目名只能由目录名解码得到——文件里**没有任何 cwd 字段**
/// （Windows 侧 87 个真实 session / 27724 行实证）。
struct GrokParser: AgentSessionParser {
    let agent = AgentKind.grok
    let root = ParserSupport.home("~/.grok/sessions")

    func watchRoots() -> [URL] { [root] }

    func makeContext(for url: URL) -> ParsedFileContext? {
        // ⚠ 必须锚定到 `updates.jsonl`：同一棵会话树下并存 6 种 `.jsonl`
        // （chat_history / events / updates / rewind_points / hunk_records /
        // prompt_history），宽松匹配会把同一轮对话重复摄取（Kimi A1 同类教训）。
        guard url.lastPathComponent == "updates.jsonl",
              url.path.hasPrefix(root.path) else { return nil }

        let sessionDir = url.deletingLastPathComponent()
        let projectDir = sessionDir.deletingLastPathComponent()
        // 层级也要对齐 Windows 侧正则（sessions/<cwd>/<uuid>/updates.jsonl，
        // 中间恰好两段），免得未来 Grok 换布局时两端行为悄悄分叉。
        guard projectDir.deletingLastPathComponent().path == root.path else { return nil }

        let sessionId = sessionDir.lastPathComponent
        let cwd = projectDir.lastPathComponent.removingPercentEncoding
            ?? projectDir.lastPathComponent
        var context = ParsedFileContext(
            url: url, agent: .grok, sessionId: sessionId,
            project: Self.projectName(fromCwd: cwd), cwd: cwd)
        if cwd == AppSettings.summarizerScratchDir { context.disabled = true }
        return context
    }

    /// 目录名是**百分号编码的工作目录绝对路径**
    /// （mac：`%2FUsers%2Fme%2Fdev%2Fmy-app` → `/Users/me/dev/my-app`；
    /// Windows：`F%3A%5C…%5Chawk-watcher` → `F:\…\hawk-watcher`）。
    /// 反斜杠也归一成 `/` 再取末段，这样同一份语料在两端解出同一个项目名
    /// （与 win `ParserUtil.ProjectNameFromCwd` 同口径）。
    static func projectName(fromCwd cwd: String) -> String {
        var normalized = cwd.replacingOccurrences(of: "\\", with: "/")
        while normalized.count > 1 && normalized.hasSuffix("/") { normalized.removeLast() }
        let leaf = normalized.split(separator: "/").last.map(String.init) ?? ""
        return leaf.isEmpty ? "grok" : leaf
    }

    func parse(line: String, context: inout ParsedFileContext) -> [SessionEvent] {
        guard !context.disabled, let obj = ParserSupport.json(line) else { return [] }

        // ⚠ `timestamp` 是 unix **整秒**（数字），不是 ISO8601——
        // `ParserSupport.timestamp(_:carriedBy:)` 解不了，这里走数值分支，
        // 但回退口径完全一致：解不出就顺延本文件上一条，从没有过则丢弃该行
        // （绝不回退「当前时间」：ts 参与唯一键，重扫必产生重复行）。
        let lineTimestamp = Self.timestamp(obj["timestamp"], carriedBy: &context)

        guard let params = obj["params"] as? [String: Any] else { return [] }
        if let sid = params["sessionId"] as? String, !sid.isEmpty { context.sessionId = sid }
        guard let update = params["update"] as? [String: Any],
              let kind = update["sessionUpdate"] as? String else { return [] }

        switch kind {
        case "user_message_chunk":
            guard let ts = lineTimestamp, let raw = Self.contentText(update) else { return [] }
            let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            // L1 用双端共享清单；本机语料实际命中的是 `<system-reminder>` 后台任务
            // 完成回执（92 条用户消息里 4 条），不是人打的字。
            guard !ParserSupport.isIgnoredContent(text) else { return [] }
            let cmd = UserCommand(
                agent: .grok,
                project: context.project,
                cwd: context.cwd,
                sessionId: context.sessionId,
                timestamp: ts,
                text: text,
                sourceFile: context.url.path)
            return [.userCommand(cmd)]

        case "agent_message_chunk":
            // 名字里是 chunk，实际一条即一条完整消息。工具调用之间的进度旁白也走
            // 这个通道（实测 532 条对 57 个 turn_completed），只有轮次结束前的最后
            // 一条是给用户的答复 → 一路覆盖暂存。
            guard let text = Self.contentText(update), !text.isEmpty else { return [] }
            context.pendingAssistantText = text
            context.pendingAssistantTimestamp = lineTimestamp
            return []

        case "turn_completed":
            let text = context.pendingAssistantText
            let ts = context.pendingAssistantTimestamp ?? lineTimestamp
            context.pendingAssistantText = nil
            context.pendingAssistantTimestamp = nil
            // 重启续扫时 offset 若已越过这些行，本轮无暂存 → 不产出结果行
            // （宁可少一条，也不拿进度旁白冒充答复）。
            guard let text, !text.isEmpty, let ts else { return [] }
            return [.assistantText(agent: .grok, sessionId: context.sessionId, timestamp: ts, text: text)]

        default:
            // tool_call / tool_call_update / hook_execution / agent_thought_chunk /
            // plan / task_backgrounded / task_completed / session_recap → 全部忽略。
            // ⚠ `task_completed` 是子任务/工具完成，不是轮次完成，不可当结果行。
            return []
        }
    }

    /// `update.content.text`（content 是单个对象，不是数组）。
    private static func contentText(_ update: [String: Any]) -> String? {
        (update["content"] as? [String: Any])?["text"] as? String
    }

    /// unix 整秒 → Date，回退口径与 `ParserSupport.timestamp` 一致。
    private static func timestamp(_ raw: Any?, carriedBy context: inout ParsedFileContext) -> Date? {
        var secs: Int64?
        if let n = raw as? Int64 { secs = n }
        else if let n = raw as? Int { secs = Int64(n) }
        else if let n = raw as? Double { secs = Int64(n) }
        else if let s = raw as? String { secs = Int64(s) }
        // 合法 unix 秒区间之外（脏数据 / 占位 0）判为解析失败，走顺延。
        if let s = secs, s > 0, s <= 253_402_300_799 {
            let parsed = Date(timeIntervalSince1970: TimeInterval(s))
            context.lastTimestamp = parsed
            return parsed
        }
        return context.lastTimestamp
    }
}
