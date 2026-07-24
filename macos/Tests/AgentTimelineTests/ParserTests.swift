import XCTest
@testable import AgentTimeline

final class ParserTests: XCTestCase {

    // MARK: - Claude

    private func claudeContext() -> ParsedFileContext {
        ParsedFileContext(
            url: URL(fileURLWithPath: NSHomeDirectory() + "/.claude/projects/-Users-x-proj/abc-123.jsonl"),
            agent: .claude, sessionId: "abc-123", project: "-Users-x-proj", cwd: nil)
    }

    func testClaudeUserCommand() {
        let parser = ClaudeParser()
        var ctx = claudeContext()
        let line = """
        {"type":"user","message":{"role":"user","content":"帮我实现 T-PLUGIN-00 的调度器"},"uuid":"u1","timestamp":"2026-06-26T12:49:57.948Z","cwd":"/Users/x/proj","sessionId":"abc-123","gitBranch":"main"}
        """
        let events = parser.parse(line: line, context: &ctx)
        guard case .userCommand(let cmd)? = events.first else {
            return XCTFail("expected userCommand, got \(events)")
        }
        XCTAssertEqual(cmd.text, "帮我实现 T-PLUGIN-00 的调度器")
        XCTAssertEqual(cmd.project, "proj")
        XCTAssertEqual(cmd.sessionId, "abc-123")
        XCTAssertEqual(cmd.agent, .claude)
    }

    func testClaudeFiltersMetaAndCaveatsAndToolResults() {
        let parser = ClaudeParser()
        var ctx = claudeContext()
        let lines = [
            // isMeta
            #"{"type":"user","isMeta":true,"message":{"role":"user","content":"meta"},"timestamp":"2026-06-26T12:49:57.948Z"}"#,
            // sidechain
            #"{"type":"user","isSidechain":true,"message":{"role":"user","content":"side"},"timestamp":"2026-06-26T12:49:57.948Z"}"#,
            // local command caveat
            #"{"type":"user","message":{"role":"user","content":"<local-command-caveat>x</local-command-caveat>"},"timestamp":"2026-06-26T12:49:57.948Z"}"#,
            // tool_result-only content array
            #"{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"out"}]},"timestamp":"2026-06-26T12:49:57.948Z"}"#,
            // system-reminder
            #"{"type":"user","message":{"role":"user","content":"<system-reminder>r</system-reminder>"},"timestamp":"2026-06-26T12:49:57.948Z"}"#,
        ]
        for line in lines {
            XCTAssertTrue(parser.parse(line: line, context: &ctx).isEmpty, "should filter: \(line.prefix(60))")
        }
    }

    func testClaudeAssistantText() {
        let parser = ClaudeParser()
        var ctx = claudeContext()
        let line = """
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"完成了调度器"}]},"timestamp":"2026-06-26T12:50:00.000Z","sessionId":"abc-123"}
        """
        let events = parser.parse(line: line, context: &ctx)
        guard case .assistantText(_, _, _, let text)? = events.first else {
            return XCTFail("expected assistantText")
        }
        XCTAssertEqual(text, "完成了调度器")
    }

    func testClaudeSummarizerSessionDisabled() {
        let parser = ClaudeParser()
        var ctx = claudeContext()
        let scratch = AppSettings.summarizerScratchDir
        let line = """
        {"type":"user","message":{"role":"user","content":"内部摘要 prompt"},"timestamp":"2026-06-26T12:49:57.948Z","cwd":"\(scratch)","sessionId":"abc-123"}
        """
        XCTAssertTrue(parser.parse(line: line, context: &ctx).isEmpty)
        XCTAssertTrue(ctx.disabled)
    }

    func testClaudeQueuedCommandAttachment() {
        let parser = ClaudeParser()
        var ctx = claudeContext()
        let line = """
        {"type":"attachment","attachment":{"type":"queued_command","prompt":"测试完成后更新版本号并 push"},"timestamp":"2026-07-06T19:00:21.000Z","sessionId":"abc-123"}
        """
        guard case .userCommand(let cmd)? = parser.parse(line: line, context: &ctx).first else {
            return XCTFail("queued_command attachment should surface as a user command")
        }
        XCTAssertEqual(cmd.text, "测试完成后更新版本号并 push")

        let noise = """
        {"type":"attachment","attachment":{"type":"queued_command","prompt":"<task-notification>x</task-notification>"},"timestamp":"2026-07-06T19:00:21.000Z"}
        """
        XCTAssertTrue(parser.parse(line: noise, context: &ctx).isEmpty, "meta blobs stay filtered")
    }

    // MARK: - Codex

    func testCodexFlow() {
        let parser = CodexParser()
        let url = URL(fileURLWithPath: NSHomeDirectory() + "/.codex/sessions/2026/04/23/rollout-2026-04-23T11-21-47-uuid.jsonl")
        var ctx = ParsedFileContext(url: url, agent: .codex, sessionId: "fallback", project: "codex", cwd: nil)

        let meta = #"{"timestamp":"2026-04-23T03:23:00.000Z","type":"session_meta","payload":{"id":"sess-1","cwd":"/Users/x/myproj","cli_version":"0.130.0"}}"#
        XCTAssertTrue(parser.parse(line: meta, context: &ctx).isEmpty)
        XCTAssertEqual(ctx.sessionId, "sess-1")
        XCTAssertEqual(ctx.project, "myproj")

        let user = #"{"timestamp":"2026-04-23T03:23:32.816Z","type":"event_msg","payload":{"type":"user_message","message":"审核边界场景 REQ-EDGE-01"}}"#
        let events = parser.parse(line: user, context: &ctx)
        guard case .userCommand(let cmd)? = events.first else {
            return XCTFail("expected userCommand")
        }
        XCTAssertEqual(cmd.sessionId, "sess-1")
        XCTAssertTrue(cmd.text.contains("REQ-EDGE-01"))

        let done = #"{"timestamp":"2026-04-23T03:30:00.000Z","type":"event_msg","payload":{"type":"task_complete","last_agent_message":"审核完成，全部满足"}}"#
        guard case .assistantText(_, _, _, let text)? = parser.parse(line: done, context: &ctx).first else {
            return XCTFail("expected assistantText")
        }
        XCTAssertEqual(text, "审核完成，全部满足")
    }

    func testCodexFiltersInjectedContext() {
        let parser = CodexParser()
        let url = URL(fileURLWithPath: NSHomeDirectory() + "/.codex/sessions/2026/04/23/rollout-x.jsonl")
        var ctx = ParsedFileContext(url: url, agent: .codex, sessionId: "s", project: "p", cwd: nil)
        let line = #"{"timestamp":"2026-04-23T03:23:32.816Z","type":"event_msg","payload":{"type":"user_message","message":"<user_instructions>injected</user_instructions>"}}"#
        XCTAssertTrue(parser.parse(line: line, context: &ctx).isEmpty)
    }

    // MARK: - Kimi

    func testKimiTurnBegin() {
        let parser = KimiParser()
        let url = URL(fileURLWithPath: NSHomeDirectory() + "/.kimi/sessions/hash1/sess-uuid/wire.jsonl")
        var ctx = ParsedFileContext(url: url, agent: .kimi, sessionId: "sess-uuid", project: "kimi:hash1", cwd: nil)

        XCTAssertTrue(parser.parse(line: #"{"type": "metadata", "protocol_version": "1.10"}"#, context: &ctx).isEmpty)

        let turn = #"{"timestamp": 1779551316.59918, "message": {"type": "TurnBegin", "payload": {"user_input": [{"type": "text", "text": "生成一个 HTML 页面"}]}}}"#
        guard case .userCommand(let cmd)? = parser.parse(line: turn, context: &ctx).first else {
            return XCTFail("expected userCommand")
        }
        XCTAssertEqual(cmd.text, "生成一个 HTML 页面")
        XCTAssertEqual(cmd.sessionId, "sess-uuid")

        let slash = #"{"timestamp": 1779551316.0, "message": {"type": "TurnBegin", "payload": {"user_input": [{"type": "text", "text": "/model"}]}}}"#
        XCTAssertTrue(parser.parse(line: slash, context: &ctx).isEmpty, "slash commands are UI actions")
    }

    func testKimiContentPartBecomesResult() {
        let parser = KimiParser()
        let url = URL(fileURLWithPath: NSHomeDirectory() + "/.kimi/sessions/hash1/sess-uuid/wire.jsonl")
        var ctx = ParsedFileContext(url: url, agent: .kimi, sessionId: "sess-uuid", project: "kimi:hash1", cwd: nil)
        let line = #"{"timestamp": 1779551320.0, "message": {"type": "ContentPart", "payload": {"type": "text", "text": "已生成 HTML 文件"}}}"#
        guard case .assistantText(_, _, _, let text)? = parser.parse(line: line, context: &ctx).first else {
            return XCTFail("ContentPart text should surface as assistantText")
        }
        XCTAssertEqual(text, "已生成 HTML 文件")
    }

    // MARK: - Codenames

    func testCodenameDetector() {
        let hits = CodenameDetector.detect(in: "完成 T-PLUGIN-00 与 REQ-AUTH-3，注意 UTF-8 不算，M-1 太短也不算")
        XCTAssertTrue(hits.contains("T-PLUGIN-00"))
        XCTAssertTrue(hits.contains("REQ-AUTH-3"))
        XCTAssertFalse(hits.contains("UTF-8"))
        XCTAssertFalse(hits.contains("M-1"))
    }

    func testSummaryPromptParse() {
        let raw = """
        好的，以下是结果：
        ```json
        {"title":"实现调度器","keyPoints":["支持并发","失败重试"],"codenames":[{"name":"T-PLUGIN-00","definition":"插件调度器任务"}],"resultLine":null}
        ```
        """
        let summary = SummaryPrompt.parse(raw, engine: .cli)
        XCTAssertEqual(summary?.title, "实现调度器")
        XCTAssertEqual(summary?.keyPoints.count, 2)
        XCTAssertEqual(summary?.codenames.first?.name, "T-PLUGIN-00")
        XCTAssertEqual(summary?.engine, "cli")
    }

    func testStableIdDeterministic() {
        let ts = Date(timeIntervalSince1970: 1_700_000_000)
        let a = UserCommand(agent: .claude, project: "p", cwd: nil, sessionId: "s",
                            timestamp: ts, text: "hello", sourceFile: "/f")
        let b = UserCommand(agent: .claude, project: "其他项目名不影响", cwd: "/other", sessionId: "s",
                            timestamp: ts, text: "hello", sourceFile: "/g")
        XCTAssertEqual(a.id, b.id)
    }
}
