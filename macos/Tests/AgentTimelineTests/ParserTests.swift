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

    // MARK: - Codename lifecycle（用户场景回归）

    /// 场景1：会话中把需求编号成 N1/N2/N3，后续出现 "N2完成" "N3变更"。
    func testScenarioRequirementBatchNumbers() {
        let text = "好的，需求编号如下：\nN1: 登录页改版\nN2: 支付流程重构\nN3: 消息中心优化"
        let defs = CodenameDetector.detectDefinitions(in: text)
        XCTAssertEqual(defs.map(\.name), ["N1", "N2", "N3"])
        XCTAssertEqual(defs[1].definition, "支付流程重构")

        let known: Set<String> = ["N1", "N2", "N3"]
        let updates = CodenameDetector.detectMentions(in: "N2完成，N3变更，N1 继续推进", known: known)
        let byName = Dictionary(uniqueKeysWithValues: updates.map { ($0.name, $0.status) })
        XCTAssertEqual(byName["N2"], .completed)
        XCTAssertEqual(byName["N3"], .changed)
        XCTAssertEqual(byName["N1"], .active)
    }

    /// 场景2：任务编号 T1/T2，"T1 完成，接下去执行T2"。
    func testScenarioTaskHandoff() {
        let known: Set<String> = ["T1", "T2"]
        let updates = CodenameDetector.detectMentions(in: "T1 完成，接下去执行T2", known: known)
        let byName = Dictionary(uniqueKeysWithValues: updates.map { ($0.name, $0.status) })
        XCTAssertEqual(byName["T1"], .completed)
        XCTAssertEqual(byName["T2"], .active)
    }

    func testDefinitionFormats() {
        // 行内冒号引导（agent 回复最常见形态）
        XCTAssertEqual(
            CodenameDetector.detectDefinitions(in: "好的，编号如下：N1: 登录改版").map(\.name), ["N1"])
        // markdown 加粗列表键
        XCTAssertEqual(
            CodenameDetector.detectDefinitions(in: "- **N1**: 登录页改版").first?.name, "N1")
        // ASCII 逗号链与顿号链逐个切分
        let commaChain = CodenameDetector.detectDefinitions(in: "N1: login rework, N2: payment rework")
        XCTAssertEqual(commaChain.map(\.name), ["N1", "N2"])
        XCTAssertEqual(commaChain[0].definition, "login rework")
        // 重放展平文本（换行被替换为空格）
        let flattened = CodenameDetector.detectDefinitions(in: "编号如下： N1: 登录页改版 N2: 支付重构")
        XCTAssertEqual(flattened.map(\.name), ["N1", "N2"])
        XCTAssertEqual(flattened[1].definition, "支付重构")
    }

    func testStopListBlocksTechVocabulary() {
        XCTAssertTrue(
            CodenameDetector.detectDefinitions(in: "S3: 存储桶配置\nQ1: 一季度目标\nEC2: 计算实例").isEmpty)
        XCTAssertTrue(CodenameDetector.detect(in: "升级到 HTTP-2 和 GPT-4").isEmpty)
    }

    func testNegatedStatusKeywords() {
        let known: Set<String> = ["N2", "T3"]
        let updates = CodenameDetector.detectMentions(in: "N2 尚未完成，T3 不执行", known: known)
        for update in updates {
            XCTAssertNil(update.status, "\(update.name) 的否定语境不应产生状态")
        }
    }

    func testDefinitionIsNotSelfMention() throws {
        let dbPath = NSTemporaryDirectory() + "at-test-\(UUID().uuidString).sqlite"
        defer { try? FileManager.default.removeItem(atPath: dbPath) }
        let store = try Store(path: dbPath)
        let registry = CodenameRegistry(store: store)
        let cmd = UserCommand(agent: .claude, project: "p", cwd: nil, sessionId: "s",
                              timestamp: Date(timeIntervalSince1970: 1_700_000_000),
                              text: "N1: 完成支付重构的收尾工作", sourceFile: "/f")
        registry.processCommand(cmd)
        let entry = try XCTUnwrap(store.fetchCodenames()["N1"])
        XCTAssertEqual(entry.statusValue, .defined, "定义句身内的关键词不应翻转刚设的定义状态")
        XCTAssertEqual(entry.occurrences, 1, "定义时不应重复计数")
    }

    func testShortCodeWordBoundaryAndUnknown() {
        // "T1" inside "T12" must not match; unknown short codes never bare-match.
        let updates = CodenameDetector.detectMentions(in: "T12 完成", known: ["T1"])
        XCTAssertTrue(updates.isEmpty)
        XCTAssertTrue(CodenameDetector.detect(in: "N2完成").isEmpty, "短码不允许裸匹配进词典")
    }

    func testDefinitionRestatementFlipsToChanged() throws {
        let dbPath = NSTemporaryDirectory() + "at-test-\(UUID().uuidString).sqlite"
        defer { try? FileManager.default.removeItem(atPath: dbPath) }
        let store = try Store(path: dbPath)
        let ts = Date(timeIntervalSince1970: 1_700_000_000)
        store.defineCodename(name: "N2", definition: "支付流程重构", nodeId: "a", at: ts)
        store.defineCodename(name: "N2", definition: "支付流程重构（含退款）", nodeId: "b", at: ts.addingTimeInterval(60))
        let entry = try XCTUnwrap(store.fetchCodenames()["N2"])
        XCTAssertEqual(entry.definition, "支付流程重构（含退款）", "最新定义生效")
        XCTAssertEqual(entry.statusValue, .changed, "定义被改写应标记为变更")
        XCTAssertEqual(entry.definitionNodeId, "a", "首次定义节点保留")
        store.touchCodename(name: "N2", status: .completed, context: "N2完成", nodeId: "c", at: ts.addingTimeInterval(120))
        XCTAssertEqual(store.fetchCodenames()["N2"]?.statusValue, .completed)
    }

    func testSummaryParseWithKindAndStatus() {
        let raw = #"{"title":"批量编号需求","kind":"需求","keyPoints":[],"codenames":[{"name":"N1","definition":"登录页改版","status":"定义"},{"name":"N2","definition":"","status":"完成"}],"resultLine":null}"#
        let summary = SummaryPrompt.parse(raw, engine: .cli)
        XCTAssertEqual(summary?.kind, "需求")
        XCTAssertEqual(summary?.codenames.count, 2)
        XCTAssertEqual(summary?.codenames[1].status, "完成")
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
