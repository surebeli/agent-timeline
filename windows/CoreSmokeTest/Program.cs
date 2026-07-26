// Core smoke test — functional assertions over the WinUI-free Core layer.
// Ports the codename-lifecycle scenarios from macos Tests/AgentTimelineTests/ParserTests.swift
// (场景1 批量需求编号 N1/N2/N3, 场景2 任务交接 T1→T2, 词边界, 定义重述→变更, 摘要 kind/status
// 契约) plus store migration/replay checks. Exit code 0 = all assertions passed.

using AgentTimeline.Core;
using AgentTimeline.Core.Parsers;
using AgentTimeline.Core.Summarize;

internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static int Main()
    {
        DetectorDashBasics();
        ScenarioRequirementBatchNumbers();
        ScenarioTaskHandoff();
        DefinitionFormats();
        StopListBlocksTechVocabulary();
        NegatedStatusKeywords();
        DefinitionIsNotSelfMention();
        ShortCodeWordBoundaryAndUnknown();
        DefinitionRestatementFlipsToChanged();
        SummaryParseWithKindAndStatus();
        SummaryParseLegacyContract();
        RuleSummarizerLifecycle();
        GuessKindFallback();
        RegistryProcessTextEndToEnd();
        StoreKindColumnAndFilter();
        StoreCompoundCursorPaging();
        StoreLatestNodeId();
        ReplayRebuildsFromHistory();
        ZcodeParserBasics();
        SummaryJsonExtractionRobustness();
        ClipSurrogateSafety();

        Console.WriteLine();
        Console.WriteLine($"passed: {_passed}, failed: {Failures.Count}");
        foreach (var failure in Failures) Console.WriteLine($"  FAILED: {failure}");
        return Failures.Count == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- helpers

    private static void Check(bool condition, string name)
    {
        if (condition) _passed++;
        else Failures.Add(name);
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
    }

    private static void CheckEqual<T>(T? actual, T? expected, string name)
    {
        var ok = EqualityComparer<T?>.Default.Equals(actual, expected);
        if (!ok) name += $" (expected [{expected}], got [{actual}])";
        Check(ok, name);
    }

    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"at-core-smoke-{Guid.NewGuid():N}.sqlite");

    private static void CleanupDb(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); } catch { /* best effort */ }
        }
    }

    private static UserCommand Cmd(string text, long tsSeconds, string session = "s") =>
        new(AgentKind.Claude, "proj", session, DateTimeOffset.FromUnixTimeSeconds(tsSeconds),
            text, "/tmp/session.jsonl", 0);

    // ------------------------------------------------------------------ tests

    private static void DetectorDashBasics()
    {
        var hits = CodenameDetector.Detect("完成 T-PLUGIN-00 与 REQ-AUTH-3，注意 UTF-8 不算，M-1 太短也不算");
        Check(hits.Contains("T-PLUGIN-00"), "detect: T-PLUGIN-00 matched");
        Check(hits.Contains("REQ-AUTH-3"), "detect: REQ-AUTH-3 matched");
        Check(!hits.Contains("UTF-8"), "detect: UTF-8 stop-listed");
        Check(!hits.Contains("M-1"), "detect: M-1 too short");
    }

    /// <summary>场景1: 会话中把需求编号成 N1/N2/N3，后续出现 "N2完成" "N3变更"。</summary>
    private static void ScenarioRequirementBatchNumbers()
    {
        var text = "好的，需求编号如下：\nN1: 登录页改版\nN2: 支付流程重构\nN3: 消息中心优化";
        var defs = CodenameDetector.DetectDefinitions(text);
        CheckEqual(string.Join(",", defs.Select(d => d.Name)), "N1,N2,N3", "scenario1: N1/N2/N3 defined");
        CheckEqual(defs.Count > 1 ? defs[1].Definition : "", "支付流程重构", "scenario1: N2 definition text");

        var known = new HashSet<string> { "N1", "N2", "N3" };
        var updates = CodenameDetector.DetectMentions("N2完成，N3变更，N1 继续推进", known)
            .ToDictionary(u => u.Name, u => u.Status);
        CheckEqual(updates.GetValueOrDefault("N2"), CodenameStatus.Completed, "scenario1: N2完成 → 完成");
        CheckEqual(updates.GetValueOrDefault("N3"), CodenameStatus.Changed, "scenario1: N3变更 → 变更");
        CheckEqual(updates.GetValueOrDefault("N1"), CodenameStatus.Active, "scenario1: N1 继续推进 → 进行中");
    }

    /// <summary>场景2: 任务编号 T1/T2，"T1 完成，接下去执行T2"。</summary>
    private static void ScenarioTaskHandoff()
    {
        var known = new HashSet<string> { "T1", "T2" };
        var updates = CodenameDetector.DetectMentions("T1 完成，接下去执行T2", known)
            .ToDictionary(u => u.Name, u => u.Status);
        CheckEqual(updates.GetValueOrDefault("T1"), CodenameStatus.Completed, "scenario2: T1 → 完成");
        CheckEqual(updates.GetValueOrDefault("T2"), CodenameStatus.Active, "scenario2: T2 → 进行中");
    }

    /// <summary>行内冒号引导 / markdown 加粗 / ASCII 逗号链 / 重放展平文本 — 定义式四种形态。</summary>
    private static void DefinitionFormats()
    {
        // 行内冒号引导（agent 回复最常见形态）
        CheckEqual(
            string.Join(",", CodenameDetector.DetectDefinitions("好的，编号如下：N1: 登录改版").Select(d => d.Name)),
            "N1", "formats: inline colon lead-in");
        // markdown 加粗列表键
        CheckEqual(
            CodenameDetector.DetectDefinitions("- **N1**: 登录页改版").FirstOrDefault().Name,
            "N1", "formats: bold markdown key");
        // ASCII 逗号链逐个切分
        var commaChain = CodenameDetector.DetectDefinitions("N1: login rework, N2: payment rework");
        CheckEqual(string.Join(",", commaChain.Select(d => d.Name)), "N1,N2", "formats: ASCII comma chain names");
        CheckEqual(commaChain.Count > 0 ? commaChain[0].Definition : "", "login rework", "formats: comma chain first definition");
        // 重放展平文本（换行被替换为空格）
        var flattened = CodenameDetector.DetectDefinitions("编号如下： N1: 登录页改版 N2: 支付重构");
        CheckEqual(string.Join(",", flattened.Select(d => d.Name)), "N1,N2", "formats: flattened space-separated list");
        CheckEqual(flattened.Count > 1 ? flattened[1].Definition : "", "支付重构", "formats: flattened second definition");
    }

    private static void StopListBlocksTechVocabulary()
    {
        CheckEqual(
            CodenameDetector.DetectDefinitions("S3: 存储桶配置\nQ1: 一季度目标\nEC2: 计算实例").Count,
            0, "stoplist: S3/Q1/EC2 definitions blocked");
        CheckEqual(CodenameDetector.Detect("升级到 HTTP-2 和 GPT-4").Count, 0, "stoplist: HTTP-2/GPT-4 dash hits blocked");
        Check(CodenameDetector.IsStopped("HTTP2") && CodenameDetector.IsStopped("http-2"),
            "stoplist: dash/dot-stripped uppercase comparison");
        Check(!CodenameDetector.IsPlausibleName("1") && !CodenameDetector.IsPlausibleName("S3")
            && CodenameDetector.IsPlausibleName("N1"), "stoplist: plausibility gate");
    }

    private static void NegatedStatusKeywords()
    {
        var updates = CodenameDetector.DetectMentions("N2 尚未完成，T3 不执行", new HashSet<string> { "N2", "T3" });
        Check(updates.Count > 0 && updates.All(u => u.Status is null),
            "negation: 尚未完成 / 不执行 produce no status");
        // 无否定语境仍然正常推进
        CheckEqual(CodenameDetector.InferStatus("N2完成"), CodenameStatus.Completed, "negation: plain 完成 still fires");
    }

    private static void DefinitionIsNotSelfMention()
    {
        var dbPath = TempDbPath();
        try
        {
            using var store = new Store(dbPath);
            var registry = new CodenameRegistry(store);
            registry.ProcessText(1, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                "N1: 完成支付重构的收尾工作");
            var entry = registry.Lookup("N1");
            CheckEqual(entry?.StatusValue, CodenameStatus.Defined, "self-mention: 定义句身内关键词不翻转定义状态");
            CheckEqual(entry?.Occurrences, 1, "self-mention: 定义时不重复计数");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    private static void ShortCodeWordBoundaryAndUnknown()
    {
        // "T1" inside "T12" must not match; unknown short codes never bare-match.
        var updates = CodenameDetector.DetectMentions("T12 完成", new HashSet<string> { "T1" });
        CheckEqual(updates.Count, 0, "boundary: T1 not matched inside T12");
        CheckEqual(CodenameDetector.Detect("N2完成").Count, 0, "boundary: 短码不允许裸匹配进词典");
    }

    private static void DefinitionRestatementFlipsToChanged()
    {
        var dbPath = TempDbPath();
        try
        {
            using var store = new Store(dbPath);
            var ts = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
            store.DefineCodename("N2", "支付流程重构", nodeId: 1, at: ts);
            store.DefineCodename("N2", "支付流程重构（含退款）", nodeId: 2, at: ts.AddMinutes(1));
            var entry = store.GetCodename("N2");
            Check(entry is not null, "restate: entry exists");
            CheckEqual(entry?.Definition, "支付流程重构（含退款）", "restate: 最新定义生效");
            CheckEqual(entry?.StatusValue, CodenameStatus.Changed, "restate: 定义被改写应标记为变更");
            CheckEqual(entry?.DefiningNodeId, 1L, "restate: 首次定义节点保留");
            CheckEqual(entry?.StatusNodeId, 2L, "restate: 状态节点指向重述节点");

            store.TouchCodename("N2", CodenameStatus.Completed, "N2完成", nodeId: 3, at: ts.AddMinutes(2));
            var touched = store.GetCodename("N2");
            CheckEqual(touched?.StatusValue, CodenameStatus.Completed, "restate: touch 后状态推进为完成");
            CheckEqual(touched?.LastContext, "N2完成", "restate: last_context 记录提及片段");
            CheckEqual(touched?.StatusNodeId, 3L, "restate: 状态节点跟随 touch");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    private static void SummaryParseWithKindAndStatus()
    {
        var raw = """
            {"title":"批量编号需求","kind":"需求","keyPoints":[],"codenames":[{"name":"N1","definition":"登录页改版","status":"定义"},{"name":"N2","definition":"","status":"完成"}],"resultLine":null}
            """;
        var summary = SummaryJson.Parse(raw, SummarySource.Cli);
        Check(summary is not null, "summary kind/status: parsed");
        CheckEqual(summary?.Kind, "需求", "summary kind/status: kind == 需求");
        CheckEqual(summary?.Codenames.Count, 2, "summary kind/status: 2 codenames");
        CheckEqual(summary?.Codenames[1].Status, "完成", "summary kind/status: N2 status == 完成");

        // Invalid kind labels from the model must be dropped.
        var bad = SummaryJson.Parse("""{"title":"t","kind":"闲聊","keyPoints":[],"codenames":[],"resultLine":null}""", SummarySource.Cli);
        CheckEqual(bad?.Kind, null, "summary kind/status: invalid kind dropped");

        // Implausible codename names (list indices / tech vocab) are gated at parse time.
        var gated = SummaryJson.Parse(
            """{"title":"t","kind":"任务","keyPoints":[],"codenames":[{"name":"1","definition":"x","status":"定义"},{"name":"S3","definition":"桶","status":"定义"},{"name":"N1","definition":"","status":"提及"}],"resultLine":null}""",
            SummarySource.Cli);
        CheckEqual(gated?.Codenames.Count, 1, "summary kind/status: implausible names dropped by parser");
        CheckEqual(gated?.Codenames.FirstOrDefault()?.Name, "N1", "summary kind/status: plausible name kept");
    }

    private static void SummaryParseLegacyContract()
    {
        var raw = """
            好的，以下是结果：
            ```json
            {"title":"实现调度器","keyPoints":["支持并发","失败重试"],"codenames":[{"name":"T-PLUGIN-00","definition":"插件调度器任务"}],"resultLine":""}
            ```
            """;
        var summary = SummaryJson.Parse(raw, SummarySource.Cli);
        CheckEqual(summary?.Title, "实现调度器", "legacy contract: title");
        CheckEqual(summary?.KeyPoints.Count, 2, "legacy contract: 2 key points");
        CheckEqual(summary?.Codenames.FirstOrDefault()?.Name, "T-PLUGIN-00", "legacy contract: codename name");
        CheckEqual(summary?.Codenames.FirstOrDefault()?.Status, null, "legacy contract: no status field → null");
        CheckEqual(summary?.Kind, null, "legacy contract: no kind field → null");
    }

    private static void RuleSummarizerLifecycle()
    {
        var rule = new RuleSummarizer();
        var summary = rule.Summarize(Cmd("N1: 登录页改版\n开始实现它", 1_700_000_000));
        var def = summary.Codenames.FirstOrDefault(c => c.Name == "N1");
        CheckEqual(def?.Definition, "登录页改版", "rule: definition captured");
        CheckEqual(def?.Status, "定义", "rule: definition carries 定义 status");
        CheckEqual(summary.Kind, "任务", "rule: kind guessed 任务 (实现)");

        var dash = rule.Summarize(Cmd("推进 T-PLUGIN-00", 1_700_000_000));
        var dashDef = dash.Codenames.FirstOrDefault(c => c.Name == "T-PLUGIN-00");
        Check(dashDef is not null, "rule: dash codename still detected");
        CheckEqual(dashDef?.Status, null, "rule: bare dash mention carries no status");
    }

    private static void GuessKindFallback()
    {
        CheckEqual(RuleSummarizer.GuessKind("修复登录 bug"), "修复", "guessKind: 修复");
        CheckEqual(RuleSummarizer.GuessKind("帮我调研一下两种方案的对比"), "调研", "guessKind: 调研");
        CheckEqual(RuleSummarizer.GuessKind("讲解什么是 WAL 模式"), "学习", "guessKind: 学习");
        CheckEqual(RuleSummarizer.GuessKind("随便聊聊"), null, "guessKind: 无关键词 → null");
    }

    private static void RegistryProcessTextEndToEnd()
    {
        var dbPath = TempDbPath();
        try
        {
            using var store = new Store(dbPath);
            var registry = new CodenameRegistry(store);
            var t0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

            Check(registry.ProcessText(1, t0, "N1: 登录改版，N2: 支付重构"), "registry: definitions touch dictionary");
            Check(registry.ProcessText(2, t0.AddMinutes(1), "N2完成，N3变更，N1 继续推进"), "registry: mentions touch dictionary");

            CheckEqual(registry.Lookup("N1")?.StatusValue, CodenameStatus.Active, "registry: N1 → 进行中");
            CheckEqual(registry.Lookup("N2")?.StatusValue, CodenameStatus.Completed, "registry: N2 → 完成");
            CheckEqual(registry.Lookup("N2")?.Definition, "支付重构", "registry: N2 definition kept");
            Check(registry.Lookup("N2")?.LastContext.Contains("N2完成") == true, "registry: N2 last_context excerpt");
            Check(registry.Lookup("N3") is null, "registry: unknown 短码 N3 未裸登记");

            // LLM extraction path: status label advances the machine, 定义/提及 do not,
            // implausible names (list indices, tech vocab) are gated out entirely.
            var llm = new Summary("t", Array.Empty<string>(),
                new[]
                {
                    new CodenameDefinition("N1", "", "完成"),
                    new CodenameDefinition("N2", "", "提及"),
                    new CodenameDefinition("1", "列表序号", "定义"),
                    new CodenameDefinition("S3", "存储桶", "定义"),
                },
                null, SummarySource.Cli, "任务");
            registry.RecordFromSummary(llm, 3, t0.AddMinutes(2));
            CheckEqual(registry.Lookup("N1")?.StatusValue, CodenameStatus.Completed, "registry: LLM status 完成 applied");
            CheckEqual(registry.Lookup("N2")?.StatusValue, CodenameStatus.Completed, "registry: LLM 提及 does not regress status");
            Check(registry.Lookup("1") is null && registry.Lookup("S3") is null,
                "registry: implausible LLM names gated out");

            // Dash code born in this round: the mention pass must not double-count it.
            registry.ProcessText(4, t0.AddMinutes(3), "推进 T-PLUGIN-00");
            var born = registry.Lookup("T-PLUGIN-00");
            CheckEqual(born?.Occurrences, 1, "registry: born-this-round dash code counted once");
            CheckEqual(born?.StatusValue, CodenameStatus.Active, "registry: 推进 still infers 进行中 at birth");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    private static void StoreKindColumnAndFilter()
    {
        var dbPath = TempDbPath();
        try
        {
            using var store = new Store(dbPath);
            var rule = new RuleSummarizer();
            var cmd1 = Cmd("实现调度器重构", 1_700_000_000);
            var cmd2 = Cmd("讲解什么是 WAL", 1_700_000_100);
            var id1 = store.InsertNode(cmd1, rule.Summarize(cmd1), SummaryEngine.ComputeHash(cmd1), false);
            var id2 = store.InsertNode(cmd2, rule.Summarize(cmd2), SummaryEngine.ComputeHash(cmd2), false);
            Check(id1 > 0 && id2 > 0, "kind: nodes inserted");

            var all = store.GetRecentNodes(10);
            CheckEqual(all.Count, 2, "kind: both nodes read back");
            CheckEqual(all.FirstOrDefault(n => n.Id == id1)?.Summary.Kind, "任务", "kind: 任务 persisted");
            CheckEqual(all.FirstOrDefault(n => n.Id == id2)?.Summary.Kind, "学习", "kind: 学习 persisted");

            CheckEqual(store.GetRecentNodes(10, kind: "任务").Count, 1, "kind: filter 任务 → 1 node");
            CheckEqual(store.GetRecentNodes(10, kind: "需求").Count, 0, "kind: filter 需求 → 0 nodes");

            var pac = store.GetProjectAgentCounts();
            Check(pac.Count == 1 && pac[0].Project == "proj" &&
                  pac[0].Agent == AgentKind.Claude && pac[0].Count == 2,
                "kind: 项目-agent 分布统计（下拉来源标注）");

            // COALESCE guard: a later summary without kind must not erase the stored one.
            store.UpdateSummary(id1, new Summary("t2", Array.Empty<string>(),
                Array.Empty<CodenameDefinition>(), null, SummarySource.Cli, Kind: null), pending: false);
            CheckEqual(store.GetNode(id1)?.Summary.Kind, "任务", "kind: null update keeps existing kind");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    /// <summary>zcode transcript.jsonl 解析（实机样例逆向，2026-07-27）。</summary>
    private static void ZcodeParserBasics()
    {
        var root = Path.Combine(Path.GetTempPath(), "at-zcode-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "sess_abc12345-0000", "agent_test1");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "metadata.json"),
                """{"agentId":"agent_test1","cwd":"F:\\work\\hawk-watcher","createdAt":"2026-07-24T05:59:28.543Z","status":"completed"}""");
            var transcript = Path.Combine(dir, "transcript.jsonl");
            var parser = new ZcodeParser();
            Check(parser.CanHandle(transcript), "zcode: CanHandle transcript.jsonl");
            Check(!parser.CanHandle(Path.Combine(dir, "wire.jsonl")), "zcode: 其他文件名不接手");

            var lines = new List<RawLine>
            {
                new(0, """{"type":"turn_started","sessionId":"s","timestamp":"2026-07-24T05:59:28.599Z","payload":{"turnNumber":0,"input":"排查 T-PLUGIN-00 的构建失败"}}"""),
                new(100, """{"type":"model_streaming","timestamp":"2026-07-24T05:59:29.000Z","payload":{"chunk":"..."}}"""),
                new(200, """{"type":"turn_complete","sessionId":"s","timestamp":"2026-07-24T06:00:11.664Z","payload":{"response":"已定位：缺 dll。T-PLUGIN-00 可收口。\n详情见下。"}}"""),
                new(300, "not json at all"),
                new(400, """{"type":"turn_started","timestamp":"2026-07-24T06:01:00.000Z","payload":{"input":"   "}}"""),
            };
            var events = parser.ParseLines(transcript, lines);
            CheckEqual(events.Count, 2, "zcode: 过程事件/空输入/坏行全部跳过");

            var cmd = events.OfType<UserCommand>().FirstOrDefault();
            Check(cmd is not null, "zcode: turn_started → UserCommand");
            CheckEqual(cmd!.Project, "hawk-watcher", "zcode: 项目名取 sidecar cwd 末段");
            CheckEqual(cmd.SessionId, "agent_test1", "zcode: session = agent 目录名");
            Check(cmd.Text.Contains("T-PLUGIN-00"), "zcode: 任务原文原样保留");
            CheckEqual(cmd.Timestamp.UtcDateTime.Hour, 5, "zcode: ISO 时间戳解析");

            var done = events.OfType<TaskComplete>().FirstOrDefault();
            Check(done is not null, "zcode: turn_complete → TaskComplete");
            Check(done!.ResultLine.StartsWith("已定位"), "zcode: response 首行作 resultLine");
            Check(done.FullText!.Contains("详情见下"), "zcode: 全文保留供代号挖掘");

            // 无 sidecar 的 agent 目录 → 回退 sess 目录名前缀
            var dir2 = Path.Combine(root, "sess_abc12345-0000", "agent_test2");
            Directory.CreateDirectory(dir2);
            var events2 = new ZcodeParser().ParseLines(Path.Combine(dir2, "transcript.jsonl"),
                new List<RawLine> { new(0, """{"type":"turn_started","timestamp":"2026-07-24T07:00:00.000Z","payload":{"input":"hi"}}""") });
            CheckEqual(((UserCommand)events2[0]).Project, "sess_abc12345", "zcode: 缺 sidecar 回退 sess 前缀");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// codex exec 的 stdout 混有元信息与正文闲聊里的花括号——提取必须跳过杂讯候选、
    /// 后向优先命中末尾的摘要 JSON（M3 审计发现，旧「首 { 到末 }」提取法必坏）。
    /// </summary>
    private static void SummaryJsonExtractionRobustness()
    {
        var noisy = "workdir: C:\\x {stats: 3 files}\n闲聊提到 interface{} 这种写法\n" +
            """{"title":"修复构建脚本","kind":"修复","keyPoints":["补 dll 引用"],"codenames":[],"resultLine":null}""" +
            "\ntokens used: 123";
        var s = SummaryJson.Parse(noisy, SummarySource.Cli);
        Check(s is not null, "json: 杂讯包围下仍解析成功");
        CheckEqual(s!.Title, "修复构建脚本", "json: 命中末尾摘要对象而非杂讯花括号");
        CheckEqual(s.Kind, "修复", "json: kind 随行解析");

        var braceInString = """前导 {"title":"含}括号的标题","kind":"任务","keyPoints":[],"codenames":[],"resultLine":null} 尾注""";
        var s2 = SummaryJson.Parse(braceInString, SummarySource.Cli);
        Check(s2 is not null && s2.Title.Contains('}'), "json: 字符串内花括号不干扰配平");

        Check(SummaryJson.Parse("全是散文没有对象", SummarySource.Cli) is null, "json: 无候选返回 null");
    }

    /// <summary>UTF-16 截断不劈开代理对（emoji 尾部不出替换符乱码）。</summary>
    private static void ClipSurrogateSafety()
    {
        var atBoundary = new string('a', 19) + "😀";   // 21 code units，😀 跨在截断点上
        CheckEqual(ParserUtil.Clip(atBoundary, 20), new string('a', 19) + "…", "clip: 高代理回退一位");
        CheckEqual(ParserUtil.Clip(new string('b', 25), 20), new string('b', 20) + "…", "clip: 普通截断加省略号");
        CheckEqual(ParserUtil.Clip("short", 20), "short", "clip: 不超长原样返回");
    }

    /// <summary>
    /// 分页游标必须与排序键一致（(ts,id) 复合）：多 agent 回填按 root 串行入库会产生
    /// 「ts 更旧但 id 更大」的行，旧的 id-only 游标会把它们永久跳过（M3 实机审计发现）。
    /// 场景：新 ts 先入库拿小 id，旧 ts 后入库拿大 id，且含同 ts 并列。
    /// </summary>
    private static void StoreCompoundCursorPaging()
    {
        var dbPath = TempDbPath();
        try
        {
            using var store = new Store(dbPath);
            var rule = new RuleSummarizer();
            long Insert(string text, long ts)
            {
                var c = Cmd(text, ts);
                return store.InsertNode(c, rule.Summarize(c), SummaryEngine.ComputeHash(c), false);
            }
            var idA = Insert("A 最新", 1_700_000_300);   // 新 ts、小 id
            var idB = Insert("B 最旧一", 1_700_000_100); // 旧 ts、较大 id
            var idC = Insert("C 中间", 1_700_000_200);
            var idD = Insert("D 最旧二", 1_700_000_100); // 与 B 同 ts、更大 id

            var p1 = store.GetRecentNodes(2);
            CheckEqual(p1.Count, 2, "cursor: page1 has 2 rows");
            Check(p1[0].Id == idA && p1[1].Id == idC, "cursor: page1 = A,C (ts DESC)");

            var last = p1[^1];
            var p2 = store.GetRecentNodes(2, last.Command.Timestamp.ToUnixTimeMilliseconds(), last.Id);
            CheckEqual(p2.Count, 2, "cursor: page2 has 2 rows (old-ts high-id rows not skipped)");
            Check(p2[0].Id == idD && p2[1].Id == idB, "cursor: page2 = D,B (same-ts tie by id DESC)");

            var last2 = p2[^1];
            var p3 = store.GetRecentNodes(2, last2.Command.Timestamp.ToUnixTimeMilliseconds(), last2.Id);
            CheckEqual(p3.Count, 0, "cursor: page3 empty (no dup, no loop)");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    private static void StoreLatestNodeId()
    {
        var dbPath = TempDbPath();
        try
        {
            using var store = new Store(dbPath);
            var rule = new RuleSummarizer();
            var cmd1 = Cmd("第一条", 1_700_000_000);
            var cmd2 = Cmd("第二条", 1_700_000_100);
            var id1 = store.InsertNode(cmd1, rule.Summarize(cmd1), SummaryEngine.ComputeHash(cmd1), false);
            var id2 = store.InsertNode(cmd2, rule.Summarize(cmd2), SummaryEngine.ComputeHash(cmd2), false);

            CheckEqual(store.LatestNodeId(AgentKind.Claude, "s", DateTimeOffset.FromUnixTimeSeconds(1_700_000_050)),
                id1, "latestNode: reply lands on the node before it");
            CheckEqual(store.LatestNodeId(AgentKind.Claude, "s", DateTimeOffset.FromUnixTimeSeconds(1_700_000_200)),
                id2, "latestNode: newest wins");
            CheckEqual(store.LatestNodeId(AgentKind.Claude, "s", DateTimeOffset.FromUnixTimeSeconds(1_699_999_000)),
                null, "latestNode: nothing before the first node");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    /// <summary>
    /// The replay core the coordinator runs when AppSettings.CodenameReplayVersion is below
    /// TimelineCoordinator.CodenameReplayVersionCurrent (marker written only after
    /// completion; not exercised here because it lives in the user's settings.json).
    /// </summary>
    private static void ReplayRebuildsFromHistory()
    {
        var dbPath = TempDbPath();
        try
        {
            using var store = new Store(dbPath);
            var rule = new RuleSummarizer();
            var cmd1 = Cmd("好的，需求编号如下：\nN1: 登录页改版\nN2: 支付流程重构", 1_700_000_000);
            var cmd2 = Cmd("N1 完成，接下去执行N2", 1_700_000_060);
            var id1 = store.InsertNode(cmd1, rule.Summarize(cmd1), SummaryEngine.ComputeHash(cmd1), false);
            var id2 = store.InsertNode(cmd2, rule.Summarize(cmd2), SummaryEngine.ComputeHash(cmd2), false);
            Check(id1 > 0 && id2 > 0, "replay: history nodes inserted");

            // Pre-seed garbage to prove the replay rebuilds from scratch.
            store.RecordCodename("STALE-99", "老数据", 1, DateTimeOffset.FromUnixTimeSeconds(1_600_000_000));

            var registry = new CodenameRegistry(store);
            TimelineCoordinator.ReplayCodenames(store, registry);

            Check(registry.Lookup("STALE-99") is null, "replay: dictionary rebuilt from scratch");
            var n1 = registry.Lookup("N1");
            CheckEqual(n1?.Definition, "登录页改版", "replay: N1 definition from history");
            CheckEqual(n1?.StatusValue, CodenameStatus.Completed, "replay: N1 status from later node");
            CheckEqual(n1?.DefiningNodeId, id1, "replay: N1 defining node is the first node");
            CheckEqual(n1?.StatusNodeId, id2, "replay: N1 status node is the later node");
            CheckEqual(registry.Lookup("N2")?.StatusValue, CodenameStatus.Active, "replay: N2 → 进行中");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }
}
