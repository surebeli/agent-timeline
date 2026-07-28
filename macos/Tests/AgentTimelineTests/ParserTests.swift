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

    /// P0 回归：slash 命令回显块是该命令的唯一记录，丢弃 = 丢用户命令。
    func testClaudeSlashCommandEchoBecomesNode() {
        let parser = ClaudeParser()
        var ctx = claudeContext()

        // 字段序 A：<command-name> 先，args 为空
        let nameFirst = """
        {"type":"user","message":{"role":"user","content":"<command-name>/plugin</command-name>\\n<command-message>plugin</command-message>\\n<command-args></command-args>"},"timestamp":"2026-07-27T10:00:00.000Z","sessionId":"abc-123"}
        """
        guard case .userCommand(let a)? = parser.parse(line: nameFirst, context: &ctx).first else {
            return XCTFail("name-first 回显块应产出节点")
        }
        XCTAssertEqual(a.text, "/plugin")

        // 字段序 B：<command-message> 先（语料多数），无 args 字段
        let messageFirst = """
        {"type":"user","message":{"role":"user","content":"<command-message>codex:setup</command-message>\\n<command-name>/codex:setup</command-name>"},"timestamp":"2026-07-27T10:01:00.000Z","sessionId":"abc-123"}
        """
        guard case .userCommand(let b)? = parser.parse(line: messageFirst, context: &ctx).first else {
            return XCTFail("message-first 回显块应产出节点")
        }
        XCTAssertEqual(b.text, "/codex:setup")

        // 非空 args 是用户真实输入，必须拼回
        let withArgs = """
        {"type":"user","message":{"role":"user","content":"<command-name>/goal</command-name>\\n<command-args>把需求编号成 N1 N2</command-args>"},"timestamp":"2026-07-27T10:02:00.000Z","sessionId":"abc-123"}
        """
        guard case .userCommand(let c)? = parser.parse(line: withArgs, context: &ctx).first else {
            return XCTFail("带 args 的回显块应产出节点")
        }
        XCTAssertEqual(c.text, "/goal 把需求编号成 N1 N2")

        // 无可用命令名的回显块仍然丢弃（不产生垃圾节点）
        let broken = """
        {"type":"user","message":{"role":"user","content":"<command-message>x</command-message>"},"timestamp":"2026-07-27T10:03:00.000Z","sessionId":"abc-123"}
        """
        XCTAssertTrue(parser.parse(line: broken, context: &ctx).isEmpty)
    }

    /// 审查确认的三处双端分叉回归（Phase C 增量审查）。
    func testClaudeParityGuards() {
        let parser = ClaudeParser()
        var ctx = claudeContext()

        // ① 带前导空白的回显块必须照样转换，而不是整块 XML 泄漏成正文
        let padded = """
        {"type":"user","message":{"role":"user","content":"\\n<command-name>/foo</command-name>\\n<command-args>bar</command-args>"},"timestamp":"2026-07-27T10:00:00.000Z","sessionId":"abc-123"}
        """
        guard case .userCommand(let cmd)? = parser.parse(line: padded, context: &ctx).first else {
            return XCTFail("带前导空白的回显块应产出节点")
        }
        XCTAssertEqual(cmd.text, "/foo bar")

        // ② 子 agent 的 assistant 行不得成为父会话的结果行
        let sidechain = """
        {"type":"assistant","isSidechain":true,"message":{"role":"assistant","content":[{"type":"text","text":"子 agent 的话"}]},"timestamp":"2026-07-27T10:01:00.000Z","sessionId":"abc-123"}
        """
        XCTAssertTrue(parser.parse(line: sidechain, context: &ctx).isEmpty)
    }

    /// 规整器与 .NET 的行为对齐：哨兵回填必须 ordinal；行尾 trim 覆盖全部 Unicode 空白。
    func testNormalizerUnicodeParity() {
        // 闭合反引号后紧跟组合字符：哨兵必须回填，绝不能把私用区字符写出去
        for extender in ["\u{0301}", "\u{FE0F}", "\u{20E3}", "\u{1F3FB}"] {
            let out = TextNormalizer.normalize("结果 `stopTask`\(extender) 完成", profile: .excerpt)
            XCTAssertFalse(out.unicodeScalars.contains { $0.value == 0xE000 }, "哨兵泄漏：\(out)")
            // 逐标量比对：Swift 的 contains/== 走正则等价，"k+组合符" ≠ "k"，
            // 只有按标量序列断言才能证明保护内容被原样回填（与 win 逐字节一致）。
            XCTAssertEqual(
                Array(out.unicodeScalars.map(\.value)),
                Array("结果 stopTask\(extender) 完成".unicodeScalars.map(\.value)),
                "回填结果与 win 参照不一致：\(out)")
        }
        // 全角空格行尾：空行仍是空行（首段边界不错位）、表格与水平线仍被 skip
        XCTAssertEqual(
            ParserSupport.resultExcerpt("第一段\n\u{3000}\n第二段"), "第一段")
        XCTAssertEqual(
            TextNormalizer.normalize("| A | B |\u{3000}\n结论在此", profile: .excerpt), "结论在此")
        XCTAssertEqual(
            TextNormalizer.normalize("---\u{3000}\n真正的标题行", profile: .excerpt), "真正的标题行")
        // ICU 的额外行终止符不得让 ATX 标题规则失配
        XCTAssertEqual(
            TextNormalizer.normalize("# 标题\u{000C}正文", profile: .excerpt), "标题\u{000C}正文")
    }

    /// `!cmd` 直通 shell：输入侧是用户真实操作要保留（转 `$ cmd`），
    /// 输出侧是命令回显不是人说的话（丢弃）。与 win 同语义。
    func testClaudeBashPassthrough() {
        let parser = ClaudeParser()
        var ctx = claudeContext()

        let input = """
        {"type":"user","message":{"role":"user","content":"<bash-input>git pull --rebase</bash-input>"},"timestamp":"2026-07-28T10:00:00.000Z","sessionId":"abc-123"}
        """
        guard case .userCommand(let cmd)? = parser.parse(line: input, context: &ctx).first else {
            return XCTFail("直通命令应保留为节点")
        }
        XCTAssertEqual(cmd.text, "$ git pull --rebase")

        for output in ["<bash-stdout>Already up to date.</bash-stdout>",
                       "<bash-stderr>fatal: not a git repo</bash-stderr>"] {
            let line = """
            {"type":"user","message":{"role":"user","content":"\(output)"},"timestamp":"2026-07-28T10:00:01.000Z","sessionId":"abc-123"}
            """
            XCTAssertTrue(parser.parse(line: line, context: &ctx).isEmpty, "输出侧应丢弃：\(output)")
        }
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

    // MARK: - 双端对拍确认项（2026-07-28 四路审计）

    /// 时间戳共同规则：形态放宽 → 顺延本文件上一条 → 无前值才丢。
    func testTimestampCarryForward() {
        let parser = ClaudeParser()
        var ctx = claudeContext()

        // 文件里还没有任何成功时间戳 → 丢弃（不能凭空造 now：会跳顶 + 重扫出重复行）
        let orphan = #"{"type":"user","message":{"role":"user","content":"无时间戳"},"sessionId":"abc-123"}"#
        XCTAssertTrue(parser.parse(line: orphan, context: &ctx).isEmpty)

        // 一条正常的 → 记住它
        let good = #"{"type":"user","message":{"role":"user","content":"正常"},"timestamp":"2026-07-28T10:00:00.000Z","sessionId":"abc-123"}"#
        guard case .userCommand(let a)? = parser.parse(line: good, context: &ctx).first else {
            return XCTFail("正常行应产出")
        }
        // 之后缺时间戳的行顺延上一条（与真实邻居相邻、重扫稳定）
        guard case .userCommand(let b)? = parser.parse(line: orphan, context: &ctx).first else {
            return XCTFail("有前值时应顺延而不是丢弃")
        }
        XCTAssertEqual(a.timestamp, b.timestamp)

        // 非事件行（system / file-history-snapshot）的时间戳同样喂养回退基准
        // ——与 win 同口径：越近的锚点越好
        var ctx2 = claudeContext()
        let systemLine = #"{"type":"system","timestamp":"2026-07-28T12:00:00.000Z","sessionId":"abc-123"}"#
        XCTAssertTrue(parser.parse(line: systemLine, context: &ctx2).isEmpty, "system 行不产出节点")
        guard case .userCommand(let c)? = parser.parse(line: orphan, context: &ctx2).first else {
            return XCTFail("非事件行喂养的基准应可被顺延")
        }
        XCTAssertEqual(c.timestamp, ParserSupport.parseISO("2026-07-28T12:00:00.000Z"))

        // 形态放宽：无时区 / 空格分隔 / 纯日期都认（与 .NET TryParse 对齐）
        for form in ["2026-07-28T09:12:33", "2026-07-28 09:12:33Z", "2026-07-28"] {
            XCTAssertNotNil(ParserSupport.parseISO(form), "应认得 \(form)")
        }
    }

    /// Codex 技能回显：留徽标文字、剥本机绝对路径（跨机无效且泄漏用户名）。
    func testCodexSkillEchoConvert() {
        let input = "[$ne-git-commit:ne-git-commit](/Users/me/.codex/plugins/cache/x/SKILL.md) OMNRTCG2-74029"
        XCTAssertEqual(
            CodexParser.convertSkillEcho(input),
            "$ne-git-commit:ne-git-commit OMNRTCG2-74029")
        // 非技能回显原样返回
        let plain = "见 [文档](https://example.com/a) 说明"
        XCTAssertEqual(CodexParser.convertSkillEcho(plain), plain)
    }

    /// Codex user_message 与 Claude queued_command 都要先 trim（否则两端节点 id 都不同）。
    func testWhitespaceTrimParity() {
        let codex = CodexParser()
        let url = URL(fileURLWithPath: NSHomeDirectory() + "/.codex/sessions/2026/07/28/rollout-x.jsonl")
        var cctx = ParsedFileContext(url: url, agent: .codex, sessionId: "s", project: "p", cwd: nil)
        let line = #"{"timestamp":"2026-07-28T03:23:32.816Z","type":"event_msg","payload":{"type":"user_message","message":"  需要\n"}}"#
        guard case .userCommand(let c)? = codex.parse(line: line, context: &cctx).first else {
            return XCTFail("应产出 codex 命令")
        }
        XCTAssertEqual(c.text, "需要")

        let claude = ClaudeParser()
        var ctx = claudeContext()
        let queued = #"{"type":"attachment","attachment":{"type":"queued_command","prompt":"  排队的命令\n"},"timestamp":"2026-07-28T10:00:00.000Z","sessionId":"abc-123"}"#
        guard case .userCommand(let q)? = claude.parse(line: queued, context: &ctx).first else {
            return XCTFail("应产出排队命令")
        }
        XCTAssertEqual(q.text, "排队的命令")
    }

    // MARK: - Kimi Code（2026-07-28 换代：~/.kimi-code + wire 1.4）

    private func kimiContext(project: String = "translate-the-damn") -> ParsedFileContext {
        let url = URL(fileURLWithPath: NSHomeDirectory()
            + "/.kimi-code/sessions/wd_\(project)_483fb8b43fb8/session_7c34b3b2/agents/main/wire.jsonl")
        return ParsedFileContext(
            url: url, agent: .kimi, sessionId: "session_7c34b3b2", project: project, cwd: nil)
    }

    /// 用户命令走 turn.prompt（且 origin.kind == user），不是 append_message。
    func testKimiTurnPrompt() {
        let parser = KimiParser()
        var ctx = kimiContext()

        XCTAssertTrue(parser.parse(
            line: #"{"type":"metadata","protocol_version":"1.4","created_at":1781760567681}"#,
            context: &ctx).isEmpty)

        let prompt = #"{"type":"turn.prompt","input":[{"type":"text","text":"生成一个 HTML 页面"}],"origin":{"kind":"user"},"time":1781760567681}"#
        guard case .userCommand(let cmd)? = parser.parse(line: prompt, context: &ctx).first else {
            return XCTFail("turn.prompt 应产出用户命令")
        }
        XCTAssertEqual(cmd.text, "生成一个 HTML 页面")
        XCTAssertEqual(cmd.sessionId, "session_7c34b3b2")
        XCTAssertEqual(cmd.project, "translate-the-damn")
        // time 是毫秒 epoch
        XCTAssertEqual(cmd.timestamp.timeIntervalSince1970, 1781760567.681, accuracy: 0.01)

        // 非 user 发起的 prompt 不算用户命令
        let injected = #"{"type":"turn.prompt","input":[{"type":"text","text":"x"}],"origin":{"kind":"compact"},"time":1781760567681}"#
        XCTAssertTrue(parser.parse(line: injected, context: &ctx).isEmpty)

        // 注入上下文走的是 append_message 通道，必须无视（实测 85 条 vs 39 条真 prompt）
        let appended = #"{"type":"context.append_message","message":{"role":"user","content":[{"type":"text","text":"注入的上下文"}]}}"#
        XCTAssertTrue(parser.parse(line: appended, context: &ctx).isEmpty)

        // 裸斜杠命令是 UI 动作
        let slash = #"{"type":"turn.prompt","input":[{"type":"text","text":"/model"}],"origin":{"kind":"user"},"time":1781760567681}"#
        XCTAssertTrue(parser.parse(line: slash, context: &ctx).isEmpty)
    }

    /// 回复取 content.part 的 text；think 是思考过程必须排除。
    func testKimiContentPartTextOnly() {
        let parser = KimiParser()
        var ctx = kimiContext()

        let text = #"{"type":"context.append_loop_event","event":{"type":"content.part","part":{"type":"text","text":"已生成 HTML 文件"}},"time":1781760809581}"#
        guard case .assistantText(_, _, _, let out)? = parser.parse(line: text, context: &ctx).first else {
            return XCTFail("content.part 的 text 应产出结果行")
        }
        XCTAssertEqual(out, "已生成 HTML 文件")

        let think = #"{"type":"context.append_loop_event","event":{"type":"content.part","part":{"type":"think","text":"我先想想"}},"time":1781760809581}"#
        XCTAssertTrue(parser.parse(line: think, context: &ctx).isEmpty, "think 是思考过程不是答复")

        for other in [#"{"type":"context.append_loop_event","event":{"type":"tool.call"},"time":1}"#,
                      #"{"type":"usage.record","time":1}"#] {
            XCTAssertTrue(parser.parse(line: other, context: &ctx).isEmpty)
        }
    }

    /// 项目名取自目录名——新格式的关键改进（旧版只能显示 kimi:hash8）。
    func testKimiProjectNameFromWorkDir() {
        XCTAssertEqual(KimiParser.projectName(fromWorkDir: "wd_edit-the-damn_4e1ceee19e2e"), "edit-the-damn")
        // 项目名自身含下划线：只剥固定前缀与末段 hash
        XCTAssertEqual(KimiParser.projectName(fromWorkDir: "wd_hawk_agent-rs_dd8b1189a258"), "hawk_agent-rs")
        // 不符合模式时原样使用
        XCTAssertEqual(KimiParser.projectName(fromWorkDir: "legacy-dir"), "legacy-dir")
    }

    // MARK: - zcode（M4 实现，对齐 win CoreSmokeTest.ZcodeParserBasics）

    /// 造一棵真实形状的 zcode 目录树：sess_<uuid>/agent_<uuid>/{transcript.jsonl,metadata.json}
    private func makeZcodeTree(sess: String, agent: String, cwd: String?) throws -> URL {
        let base = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("zc-\(UUID().uuidString)")
        let agentDir = base.appendingPathComponent("\(sess)/\(agent)")
        try FileManager.default.createDirectory(at: agentDir, withIntermediateDirectories: true)
        if let cwd {
            let meta = try JSONSerialization.data(withJSONObject: ["cwd": cwd, "status": "done"])
            try meta.write(to: agentDir.appendingPathComponent("metadata.json"))
        }
        let transcript = agentDir.appendingPathComponent("transcript.jsonl")
        FileManager.default.createFile(atPath: transcript.path, contents: nil)
        return transcript
    }

    /// makeContext 的派生：sessionId 取 agent_ 目录、项目名取 sidecar cwd 末段。
    /// （内建根前缀校验在真实路径上生效，这里直接构造 context 验派生逻辑。）
    func testZcodeProjectDerivation() throws {
        // 有 sidecar：cwd 末段（win 断言用 Windows 盘符路径，这里一并验归一化）
        XCTAssertEqual(
            ParserSupport.projectName(fromCwd: "F:\\work\\hawk-watcher", fallback: "sess_abc12345"),
            "hawk-watcher")
        XCTAssertEqual(
            ParserSupport.projectName(fromCwd: "/Users/me/proj/", fallback: "fb"), "proj")
        // 无 cwd / 空白 → 回退
        XCTAssertEqual(ParserSupport.projectName(fromCwd: nil, fallback: "sess_abc12345"), "sess_abc12345")
        XCTAssertEqual(ParserSupport.projectName(fromCwd: "  ", fallback: "fb"), "fb")

        // 无 sidecar 时项目名回退 sess_ 目录名前 13 字符（"sess_"+8 位 uuid）
        let long = "sess_abc12345-6789-0000"
        XCTAssertEqual(String(long.prefix(13)), "sess_abc12345")
    }

    /// turn_started → 任务节点；turn_complete → 结果行（全文供代号挖掘）；过程事件忽略。
    func testZcodeTurnEvents() {
        let parser = ZcodeParser()
        let url = ParserSupport.home(ZcodeParser.defaultRoot)
            .appendingPathComponent("sess_abc12345/agent_test1/transcript.jsonl")
        var ctx = ParsedFileContext(
            url: url, agent: .zcode, sessionId: "agent_test1", project: "hawk-watcher", cwd: nil)

        let started = #"{"id":"1","type":"turn_started","timestamp":"2026-07-27T10:00:00.000Z","payload":{"input":"  排查启动闪退  "}}"#
        guard case .userCommand(let cmd)? = parser.parse(line: started, context: &ctx).first else {
            return XCTFail("turn_started 应产出任务节点")
        }
        XCTAssertEqual(cmd.text, "排查启动闪退", "与 win 一致：input 先 trim")
        XCTAssertEqual(cmd.sessionId, "agent_test1")
        XCTAssertEqual(cmd.project, "hawk-watcher")
        XCTAssertEqual(cmd.agent, .zcode)

        let complete = #"{"id":"2","type":"turn_complete","timestamp":"2026-07-27T10:05:00.000Z","payload":{"response":"NPE 在冷启动路径。\n\n细节见附件。"}}"#
        guard case .assistantText(_, _, _, let text)? = parser.parse(line: complete, context: &ctx).first else {
            return XCTFail("turn_complete 应产出结果")
        }
        // 解析器发未截断全文（代号挖掘吃它）；结果行由 resultExcerpt 取首段
        XCTAssertTrue(text.contains("细节见附件"), "全文不得在解析器里被截断")
        XCTAssertEqual(ParserSupport.resultExcerpt(text), "NPE 在冷启动路径。")

        // 空 input / 空 response / 过程事件都不产出
        for noise in [
            #"{"type":"turn_started","timestamp":"2026-07-27T10:00:00.000Z","payload":{"input":"   "}}"#,
            #"{"type":"turn_complete","timestamp":"2026-07-27T10:00:00.000Z","payload":{"response":""}}"#,
            #"{"type":"model_streaming","timestamp":"2026-07-27T10:00:00.000Z","payload":{"delta":"x"}}"#,
            #"{"type":"tool_call_scheduled","timestamp":"2026-07-27T10:00:00.000Z","payload":{}}"#,
        ] {
            XCTAssertTrue(parser.parse(line: noise, context: &ctx).isEmpty, "应忽略：\(noise.prefix(40))")
        }
    }

    /// 文件匹配：只认内建根下的 transcript.jsonl。
    func testZcodeFileMatching() throws {
        let parser = ZcodeParser()
        let root = ParserSupport.home(ZcodeParser.defaultRoot)
        XCTAssertNotNil(parser.makeContext(
            for: root.appendingPathComponent("sess_a/agent_b/transcript.jsonl")))
        XCTAssertNil(parser.makeContext(
            for: root.appendingPathComponent("sess_a/agent_b/metadata.json")))
        XCTAssertNil(parser.makeContext(
            for: URL(fileURLWithPath: NSHomeDirectory() + "/elsewhere/transcript.jsonl")))
        XCTAssertEqual(parser.watchRoots().map(\.lastPathComponent), ["agents"])
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

    func testAgentMonogramsMatchWindows() {
        // 双端锁定：Windows AgentKind.Monogram() 的映射（CL/CO/GR/KI/ZC）。
        XCTAssertEqual(AgentKind.claude.monogram, "CL")
        XCTAssertEqual(AgentKind.codex.monogram, "CO")
        XCTAssertEqual(AgentKind.grok.monogram, "GR")
        XCTAssertEqual(AgentKind.kimi.monogram, "KI")
        XCTAssertEqual(AgentKind.zcode.monogram, "ZC")
        XCTAssertEqual(Set(AgentKind.allCases.map(\.monogram)).count, AgentKind.allCases.count)
    }

    /// 五家 agent 的稳定键 / 展示名 / 顺序契约（对应 win `AgentKindContract`）。
    /// rawValue 落库且参与 design-token 查找，改动会让历史数据对不上；
    /// displayName 进摘要 prompt，必须与 Windows 逐字一致。
    func testAgentKindContract() {
        XCTAssertEqual(AgentKind.allCases.map(\.rawValue),
                       ["claude", "codex", "grok", "kimi", "zcode"],
                       "声明顺序即设置页展示顺序")
        XCTAssertEqual(AgentKind.grok.rawValue, "grok")
        XCTAssertEqual(AgentKind.grok.displayName, "Grok")
        XCTAssertEqual(AgentKind.grok.settingsLabel, "Grok Build")
        XCTAssertEqual(AgentKind.zcode.displayName, "ZCode", "大小写已对齐产品名")
        XCTAssertEqual(AgentKind.zcode.rawValue, "zcode", "稳定键不随展示名变（历史数据兼容）")
        XCTAssertEqual(AgentKind.allCases.map(\.settingsLabel),
                       ["Claude Code", "Codex", "Grok Build", "Kimi Code", "ZCode"])
    }

    /// Grok Build（docs/SESSION-FORMATS.md §3）。语义按 Windows 侧 87 个真实
    /// session / 27724 行实证对齐：ACP 流、unix 整秒时间戳、结果行取轮次末条 agent 消息。
    func testGrokParserTurnEvents() {
        let parser = GrokParser()
        let sessions = ParserSupport.home("~/.grok/sessions")
        let url = sessions.appendingPathComponent("%2FUsers%2Fme%2Fdev%2Fmy-app/019fa68b-e14a/updates.jsonl")

        guard var ctx = parser.makeContext(for: url) else {
            return XCTFail("updates.jsonl 应被接手")
        }
        XCTAssertEqual(ctx.project, "my-app", "项目名由 URL 编码目录名解码取末段")
        XCTAssertEqual(ctx.sessionId, "019fa68b-e14a", "sessionId 先取目录名")

        // ⚠ 必须锚定 updates.jsonl：同树下并存 6 种 .jsonl，宽松匹配会重复摄取。
        for other in ["chat_history.jsonl", "events.jsonl", "rewind_points.jsonl",
                      "hunk_records.jsonl", "prompt_history.jsonl"] {
            let sibling = url.deletingLastPathComponent().appendingPathComponent(other)
            XCTAssertNil(parser.makeContext(for: sibling), "同目录 \(other) 不接手")
        }

        // timestamp 是 unix **整秒**：1785205656 → 2026-07-28T02:27:36Z。
        // 误当毫秒会落到 1970，误走 ISO 解析则整条丢弃——两种都会被这条抓住。
        let userLine = #"{"timestamp":1785205656,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"/harnessloop:harnessloop-status"}}}}"#
        guard case .userCommand(let cmd)? = parser.parse(line: userLine, context: &ctx).first else {
            return XCTFail("user_message_chunk 应产出命令节点")
        }
        XCTAssertEqual(cmd.text, "/harnessloop:harnessloop-status")
        XCTAssertEqual(cmd.agent, .grok)
        XCTAssertEqual(cmd.sessionId, "sess-real", "sessionId 以 params.sessionId 为准")
        XCTAssertEqual(cmd.timestamp, Date(timeIntervalSince1970: 1_785_205_656))

        // 思考过程不进时间线。
        let thought = #"{"timestamp":1785205657,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"内心戏"}}}}"#
        XCTAssertTrue(parser.parse(line: thought, context: &ctx).isEmpty)

        // 一轮内多条 agent_message_chunk：前面的是进度旁白，只有末条是答复。
        let narration = #"{"timestamp":1785205660,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"正在读取状态…"}}}}"#
        XCTAssertTrue(parser.parse(line: narration, context: &ctx).isEmpty, "旁白不即时产出")

        // task_completed 是子任务完成，不是轮次完成，绝不能当结果行。
        let subtask = #"{"timestamp":1785205670,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"task_completed","task_snapshot":{"task_id":"t1"}}}}"#
        XCTAssertTrue(parser.parse(line: subtask, context: &ctx).isEmpty,
                      "task_completed 不是轮次完成")

        let answer = #"{"timestamp":1785205680,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Harnessloop status: blocked\n\n详情见下。"}}}}"#
        XCTAssertTrue(parser.parse(line: answer, context: &ctx).isEmpty)

        let turnDone = #"{"timestamp":1785205689,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"turn_completed","stop_reason":"end_turn"}}}"#
        guard case .assistantText(_, _, _, let text)? = parser.parse(line: turnDone, context: &ctx).first else {
            return XCTFail("turn_completed 应产出结果")
        }
        XCTAssertTrue(text.hasPrefix("Harnessloop status"), "结果行取轮次内最后一条 agent 消息")
        XCTAssertTrue(text.contains("详情见下"), "全文不得在解析器里被截断")

        // 轮次已结束，暂存必须清空——否则下一轮没有 agent 消息时会重复挂上一轮的答复。
        XCTAssertTrue(parser.parse(line: turnDone, context: &ctx).isEmpty, "暂存已清空，不重复产出")
    }

    /// L1 与时间戳回退（对应 win 同名断言）。
    func testGrokParserFiltersAndTimestampFallback() {
        let parser = GrokParser()
        let url = ParserSupport.home("~/.grok/sessions")
            .appendingPathComponent("%2FUsers%2Fme%2Fdev%2Fmy-app/sess-1/updates.jsonl")
        guard var ctx = parser.makeContext(for: url) else { return XCTFail("应被接手") }

        // <system-reminder> 后台任务回执不是人打的字（实测 92 条里 4 条）。
        let reminder = #"{"timestamp":1785205656,"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"<system-reminder>\nBackground task \"call-1\" completed\n</system-reminder>"}}}}"#
        XCTAssertTrue(parser.parse(line: reminder, context: &ctx).isEmpty, "<system-reminder> 跳过")

        // §4.2-14：缺时间戳顺延本文件上一条；文件里还没有过则该行不产出。
        var fresh = parser.makeContext(for: url)!
        let noTs = #"{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"无基准"}}}}"#
        XCTAssertTrue(parser.parse(line: noTs, context: &fresh).isEmpty,
                      "还没有可用时间戳时该行不产出（绝不回退当前时间）")

        let toolCall = #"{"timestamp":1785205656,"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"tool_call","content":{}}}}"#
        _ = parser.parse(line: toolCall, context: &fresh)
        guard case .userCommand(let carried)? = parser.parse(line: noTs, context: &fresh).first else {
            return XCTFail("应顺延上一条时间戳后产出")
        }
        XCTAssertEqual(carried.timestamp, Date(timeIntervalSince1970: 1_785_205_656),
                       "缺时间戳顺延本文件上一条（含被忽略事件喂养的基准）")
    }

    @MainActor
    func testRecentAgentByProject() {
        func node(_ agent: AgentKind, _ project: String, _ ts: TimeInterval) -> TimelineNode {
            TimelineNode(command: UserCommand(
                agent: agent, project: project, cwd: nil, sessionId: "s",
                timestamp: Date(timeIntervalSince1970: ts), text: "x", sourceFile: "/f"))
        }
        // 最新在前（与 store 排序一致）：web-console 最近活跃的是 claude，
        // data-pipeline 是 codex——首见即最近。
        let nodes = [
            node(.claude, "web-console", 300),
            node(.codex, "data-pipeline", 200),
            node(.kimi, "web-console", 100),
        ]
        let recent = TimelineViewModel.recentAgentByProject(nodes)
        XCTAssertEqual(recent["web-console"], .claude)
        XCTAssertEqual(recent["data-pipeline"], .codex)
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
