// Core smoke test — functional assertions over the WinUI-free Core layer.
// Ports the codename-lifecycle scenarios from macos Tests/AgentTimelineTests/ParserTests.swift
// (场景1 批量需求编号 N1/N2/N3, 场景2 任务交接 T1→T2, 词边界, 定义重述→变更, 摘要 kind/status
// 契约) plus store migration/replay checks. Exit code 0 = all assertions passed.

using System.Text.Json;
using AgentTimeline.Core;
using AgentTimeline.Core.Parsers;
using AgentTimeline.Core.Summarize;
using AgentTimeline.Core.Text;

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
        ClauseWindowBoundaries();
        CompatibilityFold();
        PanelCollapse();
        TimelinePaging();
        ItemDiffAffixes();
        LoginItemDecision();
        DefinitionIsNotSelfMention();
        ShortCodeWordBoundaryAndUnknown();
        DefinitionRestatementFlipsToChanged();
        SummaryParseWithKindAndStatus();
        SummaryParseLegacyContract();
        RuleSummarizerLifecycle();
        GuessKindFallback();
        RegistryProcessTextEndToEnd();
        CodenameSearchSemantics();
        StoreKindColumnAndFilter();
        StoreCompoundCursorPaging();
        StoreLatestNodeId();
        ReplayRebuildsFromHistory();
        ZcodeParserBasics();
        GrokParserBasics();
        AgentKindContract();
        ClaudeParserInjectionFilters();
        ClaudeIgnoredPrefixParity();
        ClaudeAssistantMultiSegment();
        ClaudeProjectCarriesAcrossLines();
        ClaudeProjectPinnedToSessionStart();
        TimestampCarryForward();
        CodexSummarizerSelfIngestion();
        ClaudeQueuedCommandRecovery();
        SummaryAttemptsAndPriority();
        ResultLineGuardAndProviderUrl();
        CodexParserSkillEcho();
        KimiWireProtocolAndPromptLimit();
        SummaryJsonExtractionRobustness();
        TextNormalizerGoldenCases();
        ResultExcerptFallback();
        ResultExcerptSkipsHeadings();
        ResultExcerptParagraph();
        ResultExcerptLeadIn();
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

        // ── 日/韩：否定在关键词**之后**，中文那套"前两字符"完全够不着（术语调研实测）
        CheckEqual(CodenameDetector.InferStatus("N2 完成していない"), null,
            "negation ja: 後置「していない」も否定と判定");
        CheckEqual(CodenameDetector.InferStatus("N2 完成できていません"), null,
            "negation ja: 敬体後置否定");
        CheckEqual(CodenameDetector.InferStatus("N2 완성하지 않았다"), null,
            "negation ko: 후치 부정 -지 않-");
        // 后置窗口遇子句边界即止：句号后的否定说的是另一件事。
        // 这一条的 ない 落在 8 字窗口**之内**、但在句号之外——只有子句截断能拦住它
        CheckEqual(CodenameDetector.InferStatus("N2 完了。残りはない"), CodenameStatus.Completed,
            "negation ja: 句点で打ち切る——次の文の否定は無関係");
        // 这一条的否定则纯粹是超距离
        CheckEqual(CodenameDetector.InferStatus("N2 完成したが、ほかの点で問題がないか確認"), CodenameStatus.Completed,
            "negation ja: 遠すぎる否定は無関係");
        // 白名单：「問題ない」整体是肯定评审语（本例无子句边界，走的确实是白名单）
        CheckEqual(CodenameDetector.InferStatus("N2 完了で問題ないです"), CodenameStatus.Completed,
            "negation ja: 「問題ない」は肯定——ホワイトリスト");

        // ── 韩语前置否定必须按**词边界**判，不能按字符
        CheckEqual(CodenameDetector.InferStatus("N2 미완성"), null,
            "negation ko: 미- 접두사는 부정");
        // 这三条是真实语料里的高频**肯定**句，按字符判会被误杀（이미 완료 实测 11,265 次）
        CheckEqual(CodenameDetector.InferStatus("N2 이미 완성"), CodenameStatus.Completed,
            "negation ko: '이미 완성'(already done) must NOT be negated");
        CheckEqual(CodenameDetector.InferStatus("N2 제안 완성"), CodenameStatus.Completed,
            "negation ko: '제안'(안 in word) must NOT be negated");
        CheckEqual(CodenameDetector.InferStatus("N2 잘못 완성"), CodenameStatus.Completed,
            "negation ko: '잘못'(못 in word) must NOT be negated");
        CheckEqual(CodenameDetector.InferStatus("N2 안 완성"), null,
            "negation ko: standalone 안 IS negation");

        // ── 兼容折叠：全角/半角形态不该让匹配失效
        CheckEqual(CodenameDetector.InferStatus("N2 ｉｎ　ｐｒｏｇｒｅｓｓ"), CodenameStatus.Active,
            "fold: 全角英数と表意スペースも in progress として一致");
        CheckEqual(CodenameDetector.InferStatus("N2 対応中"), CodenameStatus.Active, "ja: 対応中 → Active");
        CheckEqual(CodenameDetector.InferStatus("N2 대응 완료"), CodenameStatus.Completed, "ko: 완료 → Completed");
        CheckEqual(CodenameDetector.InferStatus("N2 진행 중"), CodenameStatus.Active, "ko: 진행 중 → Active");
        CheckEqual(CodenameDetector.InferStatus("N2 변경했습니다"), CodenameStatus.Changed, "ko: 변경 → Changed");
        // 日韩的「修正/수정」两义兼有，不能进 Changed——否则会盖住后面的完成义
        CheckEqual(CodenameDetector.InferStatus("N2 バグを修正済みです"), CodenameStatus.Completed,
            "ja: 修正済 → Completed（Changed に横取りされない）");

        // ── 拉丁关键词的词边界：纯子串匹配会误命中一批常见词
        CheckEqual(CodenameDetector.InferStatus("N2 networking layer"), null,
            "boundary: networking 不得命中 working");
        CheckEqual(CodenameDetector.InferStatus("N2 disclosed the plan"), null,
            "boundary: disclosed 不得命中 closed");
        CheckEqual(CodenameDetector.InferStatus("N2 swipe gesture"), null,
            "boundary: swipe 不得命中 wip");
        CheckEqual(CodenameDetector.InferStatus("N2 working on it"), CodenameStatus.Active,
            "boundary: 真正的 working 仍然命中");
        CheckEqual(CodenameDetector.InferStatus("N2 is done."), CodenameStatus.Completed,
            "boundary: 句末标点不算词边界失败");
    }

    /// <summary>
    /// 面板折叠几何（对齐 mac PanelCollapseTests 的 8 项）。窗口起不来，故几何抽成
    /// <see cref="PanelGeometry"/> 的纯函数在这里验——它只用基元类型、且落在 Core 下，
    /// 正是为了能被本工程编译（net7.0，不引 Windows App SDK，RectInt32 在这里不存在）。
    /// </summary>
    private static void PanelCollapse()
    {
        const int collapsed = 41;   // 任取一个"实测折叠高度"，几何不依赖具体值
        const int minExpanded = 320;

        // 1) 折叠：只改高度。Win32 坐标系 Y 轴向下、原点在左上 → 顶边就是 Y，
        //    "顶边不动" = 不碰 Y。这条与 mac 相反，抄公式就会让窗口往下跳。
        CheckEqual(PanelGeometry.TargetHeight(true, collapsed, 640, minExpanded), collapsed,
            "collapse: 折叠高度即目标高度");

        // 2) 展开回折叠前的高度
        CheckEqual(PanelGeometry.TargetHeight(false, collapsed, 640, minExpanded), 640,
            "collapse: 展开还原到折叠前高度");

        // 3) 往返无损
        var round = PanelGeometry.TargetHeight(
            false, collapsed, PanelGeometry.TargetHeight(true, collapsed, 580, minExpanded) == collapsed ? 580 : 0,
            minExpanded);
        CheckEqual(round, 580, "collapse: 折叠→展开往返无损");

        // 4) 异常展开高度不得把窗口还原成一条缝
        CheckEqual(PanelGeometry.TargetHeight(false, collapsed, 10, minExpanded), minExpanded,
            "collapse: 过小的展开高度抬到展开最小高度");

        // 5) 折叠高度与头部布局推导式同步。头部布局改了、这里没跟着改，这条要打红
        //    （运行时另有一道实测比对，对不上写 app.log）。
        CheckEqual(PanelGeometry.CollapsedHeightDip,
            PanelGeometry.HeaderPaddingTop + PanelGeometry.HeaderRowHeight + PanelGeometry.HeaderPaddingBottom,
            "collapse: 折叠高度 == HeaderBar 上下内边距 + 行高");
        Check(PanelGeometry.CollapsedHeightDip < PanelGeometry.ExpandedMinHeightDip,
            "collapse: 折叠高度必须远小于展开最小高度");

        // 6) 展开高度的取值优先级（W-3：老用户升级时 PanelExpandedHeight 缺失）
        CheckEqual(PanelGeometry.ResolveExpandedHeight(700, 500, 640, collapsed, minExpanded), 700,
            "collapse: 有 PanelExpandedHeight 就用它");
        CheckEqual(PanelGeometry.ResolveExpandedHeight(0, 500, 640, collapsed, minExpanded), 500,
            "collapse: 缺失时退回 WindowHeight（升级路径）");
        CheckEqual(PanelGeometry.ResolveExpandedHeight(0, collapsed, 640, collapsed, minExpanded), 640,
            "collapse: WindowHeight 本身就是折叠尺寸时不能采信，退回默认");
        CheckEqual(PanelGeometry.ResolveExpandedHeight(0, 0, 100, collapsed, minExpanded), minExpanded,
            "collapse: 默认值也过小时抬到展开最小高度");

        // 7) 回归：启动时应用折叠态不得把展开高度冲成折叠高度
        Check(!PanelGeometry.CanRecordExpandedHeight(collapsed, collapsed),
            "collapse: 当前已是折叠尺寸时**不得**记为展开高度（mac 实机踩过的坑）");
        Check(PanelGeometry.CanRecordExpandedHeight(555, collapsed),
            "collapse: 真从展开态折叠时要记下来");

        // 8) 「两个新键必须在表里」那条**不在这里**：本工程够不着 AppStrings（它在应用层，
        //    不在 Core），也不读仓库文件。改由 CI 的文案表关统一守——而且守的不是这两个键，
        //    是**代码里引用的每一个键都在表里**（scripts/check-strings.py 第 7 项），
        //    比手写两条断言覆盖面大。缺键的后果是加载器回显键名、界面上出现
        //    "header.collapse" 字样而不报错，只有跑起来才看得见，必须机器守。
    }

    /// <summary>
    /// 滚到底自动取下一页的判据（取代原先底部的「加载更多」按钮）。三道闸各有反证用例。
    /// </summary>
    private static void TimelinePaging()
    {
        const double th = PanelGeometry.LoadMoreThresholdDip;
        // 视口 500、内容 2000 → 距底 = 2000 - (offset + 500)
        bool At(double offset, bool hasMore = true, bool loading = false) =>
            PanelGeometry.ShouldLoadMore(offset, 500, 2000, hasMore, loading, th);

        Check(At(1500 - th), "paging: 刚进入阈值就预取");
        Check(At(1500), "paging: 到底当然要取");
        Check(!At(1500 - th - 1), "paging: 阈值之外不取");
        Check(!At(0), "paging: 顶部不取");

        // 闸 1：没有下一页就别问库
        Check(!At(1500, hasMore: false), "paging: HasMore=false 不取");
        // 闸 2：取页中不重入。ViewChanged 一次滚动连发多拍，且取完一页 ExtentHeight 变大
        //       又会触发一拍——不挡住会连着把整库拉完
        Check(!At(1500, loading: true), "paging: 取页进行中不重入");
        // 闸 3：内容没撑满一屏时距底恒为 0，会在启动瞬间无条件预取
        Check(!PanelGeometry.ShouldLoadMore(0, 500, 300, true, false, th),
            "paging: 内容不足一屏不取（否则启动就无条件预取）");
        Check(!PanelGeometry.ShouldLoadMore(0, 500, 500, true, false, th),
            "paging: 内容刚好一屏也不取");
        // 视口尺寸还没量出来时（首帧）不该动
        Check(!PanelGeometry.ShouldLoadMore(0, 0, 2000, true, false, th),
            "paging: 视口高度为 0 时不取");
    }

    /// <summary>
    /// 列表增量套用的前后缀差分。守的是"分页/新节点这两条日常路径不产生 Reset"——
    /// ItemsRepeater 在大滚动偏移处吃到 Reset 会把正文画成空白（实机复现过）。
    /// </summary>
    private static void ItemDiffAffixes()
    {
        static (int, int) Diff(string a, string b) =>
            ItemDiff.CommonAffixes(a.ToCharArray(), b.ToCharArray(), (x, y) => x == y);

        // 尾部追加（分页）：前缀吃满原列表，中段为 0 → 只有 Add
        var (p, s) = Diff("abc", "abcde");
        CheckEqual(p, 3, "diff: 尾部追加 前缀=全部");
        CheckEqual(3 - p - s, 0, "diff: 尾部追加 中段=0");

        // 头部插入（新节点到来）：后缀吃满原列表 → 只有 Insert(0)
        (p, s) = Diff("abc", "Xabc");
        CheckEqual(s, 3, "diff: 头部插入 后缀=全部");
        CheckEqual(3 - p - s, 0, "diff: 头部插入 中段=0");

        // 中段单点替换（"今天 · n 件"计数变了）
        (p, s) = Diff("abcd", "aXcd");
        CheckEqual(p, 1, "diff: 中段替换 前缀");
        CheckEqual(s, 2, "diff: 中段替换 后缀");

        // 整表换血（切语言）：无公共前后缀
        (p, s) = Diff("abc", "xyz");
        CheckEqual(p, 0, "diff: 整表换血 无前缀");
        CheckEqual(s, 0, "diff: 整表换血 无后缀");

        // 前后缀之和必须被较短列表夹住，否则中段算成负数、插入区间反转。
        // "aa" → "aaa"：三个位置两两都相等，朴素实现会让 prefix=2 且 suffix=2
        (p, s) = Diff("aa", "aaa");
        Check(p + s <= 2, $"diff: 前后缀之和不越界（实得 {p}+{s}）");
        (p, s) = Diff("", "abc");
        CheckEqual(p + s, 0, "diff: 空列表不炸");
        (p, s) = Diff("abc", "");
        CheckEqual(p + s, 0, "diff: 清空不炸");
    }

    /// <summary>
    /// 开机自启动的判据。前三条对齐 mac <c>LoginItem.action</c>；后面几条是 Windows 特有的
    /// ——非打包分发，用户随时可能挪目录或换构建，注册表里那条命令行会指向旧路径。
    /// </summary>
    private static void LoginItemDecision()
    {
        const string exe = @"C:\Users\me\AppData\Local\Programs\AgentTimeline\AgentTimeline.exe";
        var cmd = LoginItem.FormatCommand(exe);

        // 路径带空格必须加引号，否则开机时被按空格拆开、静默启动失败
        CheckEqual(cmd, "\"" + exe + "\"", "login: 命令行加引号");

        // mac 三条同构
        CheckEqual(LoginItem.Decide(true, null, cmd), LoginItemAction.Register,
            "login: 期望开但没注册 → Register");
        CheckEqual(LoginItem.Decide(false, cmd, cmd), LoginItemAction.Unregister,
            "login: 期望关但已注册 → Unregister");
        CheckEqual(LoginItem.Decide(true, cmd, cmd), LoginItemAction.None,
            "login: 已经一致 → None（不重复写注册表）");
        CheckEqual(LoginItem.Decide(false, null, cmd), LoginItemAction.None,
            "login: 期望关且本就没注册 → None");

        // 空白值等同未注册（注册表里留过一条空串的情况）
        CheckEqual(LoginItem.Decide(true, "   ", cmd), LoginItemAction.Register,
            "login: 空白值视为未注册");
        CheckEqual(LoginItem.Decide(false, "   ", cmd), LoginItemAction.None,
            "login: 空白值不去删");

        // 路径漂移：只判"有没有"会留下一条指向旧副本的自启动项
        var stale = LoginItem.FormatCommand(@"D:\old\AgentTimeline\AgentTimeline.exe");
        CheckEqual(LoginItem.Decide(true, stale, cmd), LoginItemAction.Register,
            "login: 注册表指向旧路径 → 重写");

        // 但等价写法不算漂移，否则每次启动都白写一次
        CheckEqual(LoginItem.Decide(true, exe, cmd), LoginItemAction.None,
            "login: 历史上没加引号的等价值 → None");
        CheckEqual(LoginItem.Decide(true, cmd.ToUpperInvariant(), cmd), LoginItemAction.None,
            "login: 大小写不同的同一路径 → None（Windows 路径不区分大小写）");
    }

    private static void CompatibilityFold()
    {
        // 半角片假名 + 分离浊点/半浊点 → 全角合成形。分类词表里 デプロイ/バグ/リリース
        // 这类关键词全是浊音，不合成就整条匹配不上。
        CheckEqual(TextNormalizer.FoldForMatch("ﾃﾞﾌﾟﾛｲ"), "デプロイ", "fold: 半角カナ＋濁点/半濁点");
        CheckEqual(TextNormalizer.FoldForMatch("ﾊﾞｸﾞ"), "バグ", "fold: 半角カナ濁点");
        CheckEqual(TextNormalizer.FoldForMatch("ＷＩＰ"), "WIP", "fold: 全角英字");
        CheckEqual(TextNormalizer.FoldForMatch("Ａ　Ｂ"), "A B", "fold: 表意スペース");
        // 合不成的浊点原样保留，不得吞字
        CheckEqual(TextNormalizer.FoldForMatch("ｱﾞ"), "ア゛", "fold: 合成できない濁点は残す");
        // 无兼容字符时必须原样返回（展示文本共用同一函数的前提）
        CheckEqual(TextNormalizer.FoldForMatch("完了 done 완료"), "完了 done 완료", "fold: 通常文字は不変");
    }

    private static void ClauseWindowBoundaries()
    {
        var known = new HashSet<string> { "N1", "N2" };
        // 韩语句子用 ASCII 句点收尾——不认它的话，邻句的状态词会串味进来
        var bleed = CodenameDetector.DetectMentions("N1 작업 시작. N2 완성.", known)
            .ToDictionary(u => u.Name, u => u.Status);
        CheckEqual(bleed["N1"], CodenameStatus.Active, "clause: ASCII 句点分句——N1 只吃到自己那句");
        CheckEqual(bleed["N2"], CodenameStatus.Completed, "clause: ASCII 句点分句——N2 同理");

        // 但版本号/文件名里的点不能当分句：窗口被截断会丢掉后面的状态词
        CheckEqual(
            CodenameDetector.DetectMentions("N1 在 v0.6.0 完成", known).First(u => u.Name == "N1").Status,
            CodenameStatus.Completed,
            "clause: v0.6.0 里的点不是分句点");

        // 韩语是 SOV，谓语在句末——向后窗口太窄会把状态词截掉
        CheckEqual(
            CodenameDetector.DetectMentions("N2 관련해서 어제 말씀드린 부분은 전부 구현 완성했습니다", known)
                .First(u => u.Name == "N2").Status,
            CodenameStatus.Completed,
            "clause: SOV 语序——向后窗口要够宽才够得着句末谓语");
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

        // 四语常开：会话语言与界面语言无关，四张表必须同时生效
        CheckEqual(RuleSummarizer.GuessKind("ログイン画面の不具合を修正して"), "修复", "guessKind ja: 不具合 → 修复");
        CheckEqual(RuleSummarizer.GuessKind("二つの方式を比較して評価したい"), "调研", "guessKind ja: 比較 → 调研");
        CheckEqual(RuleSummarizer.GuessKind("この API の仕様を説明して"), "学习", "guessKind ja: 説明 → 学习");
        CheckEqual(RuleSummarizer.GuessKind("認証まわりを実装してデプロイ"), "任务", "guessKind ja: 実装 → 任务");
        CheckEqual(RuleSummarizer.GuessKind("로그인 버그를 수정해줘"), "修复", "guessKind ko: 버그 → 修复");
        CheckEqual(RuleSummarizer.GuessKind("두 방식을 비교 검토해줘"), "调研", "guessKind ko: 비교 → 调研");
        CheckEqual(RuleSummarizer.GuessKind("이 API 가 무엇인가 설명해줘"), "学习", "guessKind ko: 설명 → 学习");
        CheckEqual(RuleSummarizer.GuessKind("인증 모듈을 구현하고 배포"), "任务", "guessKind ko: 구현 → 任务");
        CheckEqual(RuleSummarizer.GuessKind("write the PRD for search"), "需求", "guessKind en: prd → 需求");

        // 词边界：这两个词在开发语料里到处都是，不设边界会把一切命令判成 Fix
        CheckEqual(RuleSummarizer.GuessKind("rename the prefix of these files"), null,
            "guessKind boundary: prefix 不得命中 fix");
        CheckEqual(RuleSummarizer.GuessKind("add a suffix to the output"), null,
            "guessKind boundary: suffix 不得命中 fix");
        CheckEqual(RuleSummarizer.GuessKind("fix the login flow"), "修复",
            "guessKind boundary: 真正的 fix 仍然命中");

        // 折叠：全角/半角形态不该让分类失效
        CheckEqual(RuleSummarizer.GuessKind("本番へﾃﾞﾌﾟﾛｲする"), "任务", "guessKind fold: 半角カナ デプロイ");
        CheckEqual(RuleSummarizer.GuessKind("ＰＲＤ を書く"), "需求", "guessKind fold: 全角 PRD");
    }

    /// <summary>
    /// 代号词典关键字搜索的判据，**与 mac `filterCodenames` 逐字一致**
    /// （windows/SYNC-KICKOFF-PROMPT.md 2026-08-01 轮第 2 节列的 6 个场景，逐条对齐）。
    /// 判据散在 UI 事件处理里就没法验证，所以抽成 `Core/CodenameSearch`。
    /// </summary>
    private static void CodenameSearchSemantics()
    {
        var entries = new List<CodenameEntry>
        {
            new() { Name = "N1", Definition = "登录改造", LastContext = "" },
            new() { Name = "REQ-AUTH-3", Definition = "鉴权需求", LastContext = "" },
            // 名字里没有 N1，只有"最近提及"里提到——mac 实机验证命中的正是这一类
            new() { Name = "T1", Definition = "回归测试", LastContext = "N1 完成后再跑 T1" },
            new() { Name = "T-PLUGIN-00", Definition = null, LastContext = "" },
        };

        CheckEqual(CodenameSearch.Filter(entries, "").Count, 4, "search: 空查询返回全部");
        CheckEqual(CodenameSearch.Filter(entries, "   \n ").Count, 4,
            "search: 纯空白当作没有搜索词（返回全部，不是返回空）");

        var lower = CodenameSearch.Filter(entries, "req-auth-3");
        CheckEqual(lower.Count, 1, "search: 大小写不敏感");
        CheckEqual(lower.Count > 0 ? lower[0].Name : "", "REQ-AUTH-3", "search: 命中的是那一条");

        var infix = CodenameSearch.Filter(entries, "AUTH");
        CheckEqual(infix.Count, 1, "search: 子串匹配而非前缀（复合代号只记得中间一段也能搜到）");

        var byDefinition = CodenameSearch.Filter(entries, "鉴权");
        CheckEqual(byDefinition.Count, 1, "search: 匹配定义");
        CheckEqual(byDefinition.Count > 0 ? byDefinition[0].Name : "", "REQ-AUTH-3",
            "search: 只记得内容、不记得代号也能找到");

        var byContext = CodenameSearch.Filter(entries, "N1");
        CheckEqual(byContext.Count, 2, "search: 匹配最近提及（N1 本身 + lastContext 里提到它的 T1）");
        Check(byContext.Any(e => e.Name == "T1"), "search: 靠 lastContext 命中的那条在结果里");

        CheckEqual(CodenameSearch.Filter(entries, "不存在的词").Count, 0, "search: 无匹配返回空");

        // definition 为 null 的行不能把判定炸掉（win 侧 Definition 是可空的，mac 是非可空 String）
        CheckEqual(CodenameSearch.Filter(entries, "PLUGIN").Count, 1,
            "search: definition 为 null 的条目照样按名字命中，不抛");

        // 原表不被改动（过滤返回新列表）
        CheckEqual(entries.Count, 4, "search: 过滤不改动入参集合");
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

            // 最近活跃优先：同项目后来的 codex 节点虽只 1 条，也应排在 claude(2条) 前
            //（徽标 = 最近干活的 agent，实机反馈）。
            var late = new UserCommand(AgentKind.Codex, "proj", "s2",
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_500), "codex 接手", "/tmp/x.jsonl", 0);
            store.InsertNode(late, rule.Summarize(late), SummaryEngine.ComputeHash(late), false);
            var pac2 = store.GetProjectAgentCounts();
            Check(pac2.Count == 2 && pac2[0].Agent == AgentKind.Codex && pac2[0].Count == 1 &&
                  pac2[1].Agent == AgentKind.Claude && pac2[1].Count == 2 &&
                  pac2[0].LastTs > pac2[1].LastTs,
                "kind: 分布按最近活跃排序（后来者居上）");

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

            // §4.2-14 共同规则：不再用 UtcNow 回退——缺/坏时间戳顺延本文件上一条，
            // 无前值则该行不产出（与 mac、与本端 Claude/Codex 同口径）。
            var tsPath = Path.Combine(dir, "transcript.jsonl");
            var noBaseline = parser.ParseLines(tsPath, new[]
            {
                new RawLine(0, """{"type":"turn_started","payload":{"input":"无基准"}}"""),
            });
            Check(noBaseline.Count == 0, "zcode/W-e: 文件里还没有可用时间戳时该行不产出");

            var carried = parser.ParseLines(tsPath, new[]
            {
                // 过程事件也喂养基准（任意行口径）
                new RawLine(0, """{"type":"model_streaming","timestamp":"2026-07-27T10:00:00.000Z","payload":{}}"""),
                new RawLine(1, """{"type":"turn_started","payload":{"input":"顺延上一条"}}"""),
            });
            Check(carried.Count == 1 && carried[0] is UserCommand u2
                  && u2.Timestamp == DateTimeOffset.Parse("2026-07-27T10:00:00.000Z"),
                  "zcode/W-e: 缺时间戳顺延本文件上一条（含过程事件喂养的基准）");
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
    /// Grok Build（docs/SESSION-FORMATS.md §3）。路径与语义均按本机 87 个真实 session
    /// （27724 行）实证：ACP 流、unix 整秒时间戳、结果行取轮次末条 agent 消息。
    /// SeedFromPath 只做字符串运算不碰文件，故这里用纯路径构造，跨平台安全。
    /// </summary>
    private static void GrokParserBasics()
    {
        var parser = new GrokParser();
        string SessionFile(string encodedCwd, string name) => Path.Combine(
            Path.GetTempPath(), ".grok", "sessions", encodedCwd,
            "019fa68b-e14a-7951-ada9-c181552cbd27", name);

        var winCwdDir = "F%3A%5Cworkspace%5Cproject%5Chawk-watcher";
        var path = SessionFile(winCwdDir, "updates.jsonl");
        Check(parser.CanHandle(path), "grok: CanHandle updates.jsonl");

        // 同一棵树下并存 6 种 .jsonl（实测 chat_history 91 / events 91 / updates 87 /
        // rewind_points 81 / hunk_records 4 / prompt_history 3）——只能认 updates.jsonl，
        // 否则同一轮对话被重复摄取（Kimi A1 同类教训）。
        foreach (var other in new[]
                 { "chat_history.jsonl", "events.jsonl", "rewind_points.jsonl",
                   "hunk_records.jsonl", "prompt_history.jsonl" })
        {
            Check(!parser.CanHandle(SessionFile(winCwdDir, other)),
                $"grok: 同目录 {other} 不接手");
        }

        // 时间戳是 unix **整秒**（非 ISO8601、非毫秒）：1785205656 → 2026-07-28T02:27:36Z。
        // 若误当毫秒解会落到 1970 年，误走 ISO 解析则整条丢弃——两种都会被下面这条抓住。
        var events = parser.ParseLines(path, new List<RawLine>
        {
            new(0, """{"timestamp":1785205656,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"/harnessloop:harnessloop-status"}}}}"""),
            new(100, """{"timestamp":1785205657,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"内心戏不该进时间线"}}}}"""),
            new(200, """{"timestamp":1785205660,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"正在读取状态…"}}}}"""),
            new(300, """{"timestamp":1785205670,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"task_completed","task_snapshot":{"task_id":"t1"}}}}"""),
            new(400, """{"timestamp":1785205680,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Harnessloop status: blocked\n\n详情见下。"}}}}"""),
            new(500, """{"timestamp":1785205689,"method":"session/update","params":{"sessionId":"sess-real","update":{"sessionUpdate":"turn_completed","stop_reason":"end_turn"}}}"""),
            new(600, "not json at all"),
        });

        var cmd = events.OfType<UserCommand>().FirstOrDefault();
        Check(cmd is not null, "grok: user_message_chunk → UserCommand");
        CheckEqual(cmd!.Text, "/harnessloop:harnessloop-status", "grok: 命令原文原样保留");
        CheckEqual(cmd.Project, "hawk-watcher", "grok: 项目名由 URL 编码目录名解码取末段");
        CheckEqual(cmd.SessionId, "sess-real", "grok: sessionId 取 params.sessionId");
        CheckEqual(cmd.Timestamp.UtcDateTime,
            new DateTime(2026, 7, 28, 2, 27, 36, DateTimeKind.Utc), "grok: timestamp 按 unix 整秒解析");

        var done = events.OfType<TaskComplete>().FirstOrDefault();
        Check(done is not null, "grok: turn_completed → TaskComplete");
        Check(done!.ResultLine.StartsWith("Harnessloop status"),
            "grok: 结果行取轮次内**最后一条** agent 消息（不是进度旁白）");
        Check(done.FullText!.Contains("详情见下"), "grok: 全文保留供代号挖掘");
        CheckEqual(events.Count, 2, "grok: thought/task_completed/坏行全部跳过");

        // task_completed 是子任务完成，单独出现绝不能产出结果行。
        var subtaskOnly = new GrokParser().ParseLines(SessionFile(winCwdDir, "updates.jsonl"), new List<RawLine>
        {
            new(0, """{"timestamp":1785205660,"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"旁白"}}}}"""),
            new(100, """{"timestamp":1785205670,"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"task_completed","task_snapshot":{"task_id":"t1"}}}}"""),
        });
        CheckEqual(subtaskOnly.Count, 0, "grok: task_completed 不是轮次完成，不产出结果行");

        // L1：<system-reminder> 是后台任务回执（实测 92 条用户消息里 4 条），非人类输入。
        var filtered = new GrokParser().ParseLines(path, new List<RawLine>
        {
            new(0, """{"timestamp":1785205656,"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"<system-reminder>\nBackground task \"call-1\" completed (exit code 0)\n</system-reminder>"}}}}"""),
        });
        CheckEqual(filtered.Count, 0, "grok/L1: <system-reminder> 后台回执跳过");

        // mac 侧目录形态：%2FUsers%2F… → /Users/…，同一套解码取末段。
        var posix = new GrokParser().ParseLines(
            SessionFile("%2FUsers%2Fme%2Fdev%2Fmy-app", "updates.jsonl"), new List<RawLine>
            {
                new(0, """{"timestamp":1785205656,"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"hi"}}}}"""),
            });
        CheckEqual(((UserCommand)posix[0]).Project, "my-app", "grok: POSIX 形态目录名同样解码（mac 侧口径）");

        // §4.2-14 共同规则：缺时间戳顺延本文件上一条，无前值则不产出——绝不回退 UtcNow。
        var noBaseline = new GrokParser().ParseLines(path, new List<RawLine>
        {
            new(0, """{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"无基准"}}}}"""),
        });
        CheckEqual(noBaseline.Count, 0, "grok/W-e: 文件里还没有可用时间戳时该行不产出");

        var carried = new GrokParser().ParseLines(path, new List<RawLine>
        {
            new(0, """{"timestamp":1785205656,"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"tool_call","content":{}}}}"""),
            new(100, """{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"顺延上一条"}}}}"""),
        });
        Check(carried.Count == 1 && carried[0] is UserCommand c2
              && c2.Timestamp == DateTimeOffset.FromUnixTimeSeconds(1785205656),
              "grok/W-e: 缺时间戳顺延本文件上一条（含被忽略事件喂养的基准）");
    }

    /// <summary>
    /// 五家 agent 的稳定键 / 徽标 / 展示名契约。Key() 落库且参与 design-token 查找，
    /// 改动会让历史数据对不上；DisplayName 进摘要 prompt，必须与 mac 端逐字一致。
    /// </summary>
    private static void AgentKindContract()
    {
        CheckEqual(AgentKind.Grok.Key(), "grok", "agent: Grok 稳定键");
        CheckEqual(AgentKind.Grok.Monogram(), "GR", "agent: Grok 徽标缩写");
        CheckEqual(AgentKind.Grok.DisplayName(), "Grok", "agent: Grok 展示名");
        Check(AgentKindExtensions.FromKey("grok") == AgentKind.Grok, "agent: grok 键回解");
        CheckEqual(AgentKind.Zcode.DisplayName(), "ZCode", "agent: ZCode 大小写已对齐产品名");
        CheckEqual(AgentKind.Zcode.Key(), "zcode", "agent: ZCode 稳定键不随展示名变（历史数据兼容）");

        // 五家全覆盖：键唯一、徽标唯一，且每家都能由键回解。
        var kinds = new[] { AgentKind.Claude, AgentKind.Codex, AgentKind.Grok, AgentKind.Kimi, AgentKind.Zcode };
        CheckEqual(kinds.Select(k => k.Key()).Distinct().Count(), 5, "agent: 五家稳定键互不相同");
        CheckEqual(kinds.Select(k => k.Monogram()).Distinct().Count(), 5, "agent: 五家徽标缩写互不相同");
        foreach (var k in kinds)
        {
            Check(AgentKindExtensions.FromKey(k.Key()) == k, $"agent: {k.Key()} 键往返一致");
        }
    }

    /// <summary>
    /// harness 注入块整条跳过 + 命令回显块两种字段序都转换（实机语料普查:
    /// task-notification 793 次泄漏、command-message 先行 60/171,docs/TEXT-NORMALIZATION.md）。
    /// </summary>
    private static void ClaudeParserInjectionFilters()
    {
        var parser = new ClaudeParser();
        var path = Path.Combine(Path.GetTempPath(), ".claude", "projects", "-demo", "s.jsonl");
        string Line(string content) =>
            "{\"type\":\"user\",\"timestamp\":\"2026-07-27T10:00:00.000Z\",\"sessionId\":\"s\",\"cwd\":\"C:/w/demo\"," +
            "\"message\":{\"role\":\"user\",\"content\":" + JsonSerializer.Serialize(content) + "}}";

        var lines = new List<RawLine>
        {
            new(0, Line("<task-notification>\n<task-id>x</task-id>\n<status>completed</status>\n</task-notification>")),
            new(1, Line("<local-command-stdout>Set model to \u001b[1mFable\u001b[22m</local-command-stdout>")),
            new(2, Line("Caveat: The messages below were generated by the user while running local commands.")),
            new(3, Line("[Request interrupted by user]")),
            new(4, Line("This session is being continued from a previous conversation that ran out of context.")),
            new(5, Line("<command-message>hopper:continue</command-message>\n<command-name>/hopper:continue</command-name>\n<command-args>继续推进 T2</command-args>")),
            new(6, Line("<command-name>/model</command-name>\n<command-message>model</command-message>\n<command-args></command-args>")),
            // `!cmd` 直通 shell：输入是用户真实操作（转 "$ cmd"），输出不是
            new(7, Line("<bash-input>git pull</bash-input>")),
            new(8, Line("<bash-stdout>From https://github.com/x/y\n   a..b  main</bash-stdout>")),
            new(9, Line("<bash-stderr>fatal: not a git repo</bash-stderr>")),
            new(10, Line("正常的用户命令原文")),
        };
        var events = parser.ParseLines(path, lines).OfType<UserCommand>().ToList();
        CheckEqual(events.Count, 4, "claude filter: 注入块与 bash 输出全部跳过");
        CheckEqual(events[0].Text, "/hopper:continue 继续推进 T2", "claude filter: command-message 先行块转换并保留 args");
        CheckEqual(events[1].Text, "/model", "claude filter: 空 args 只留命令名");
        CheckEqual(events[2].Text, "$ git pull", "claude filter: bash-input 转 \"$ cmd\" 保留");
        CheckEqual(events[3].Text, "正常的用户命令原文", "claude filter: 正常命令不受影响");
    }

    /// <summary>claude 会话行的公共骨架：type / 时间戳 / cwd / message.content 都可控。</summary>
    private static string ClaudeUserLine(string content, string? iso = "2026-01-15T10:00:00.000Z",
        string? cwd = "C:/w/demo") =>
        "{\"type\":\"user\",\"sessionId\":\"s\"" +
        (iso is null ? "" : ",\"timestamp\":" + JsonSerializer.Serialize(iso)) +
        (cwd is null ? "" : ",\"cwd\":" + JsonSerializer.Serialize(cwd)) +
        ",\"message\":{\"role\":\"user\",\"content\":" + JsonSerializer.Serialize(content) + "}}";

    private static string ClaudeSessionPath(string slug = "-demo") =>
        Path.Combine(Path.GetTempPath(), ".claude", "projects", slug, "s.jsonl");

    /// <summary>
    /// W-b：L1 忽略前缀表逐字对齐 mac `ParserSupport.ignoredPrefixes`（11 条、裸标签不带 '>'）。
    ///
    /// win 此前写成带 '>' 的 9 条：① harness 给注入块带属性时
    /// （`<system-reminder priority="high">`、`<bash-stdout exit="0">`）前缀匹配不上，
    /// 整块 XML 变成垃圾"用户命令"节点；② `<user_instructions>` / `<environment_context>`
    /// 根本不在表里，claude 通道整批漏网（codex 通道早有过滤）。mac 侧两类都正确丢弃。
    /// </summary>
    private static void ClaudeIgnoredPrefixParity()
    {
        void Dropped(string content, string name)
        {
            // 每例独立 parser：避免跨例复用 per-path 上下文影响判定
            var got = new ClaudeParser()
                .ParseLines(ClaudeSessionPath(), new List<RawLine> { new(0, ClaudeUserLine(content)) })
                .OfType<UserCommand>().ToList();
            CheckEqual(got.Count, 0, name);
        }

        // ① 表里此前缺失的两条（mac 有、win 无）
        Dropped("<user_instructions>\n本项目一律用中文回答。\n</user_instructions>",
            "W-b: <user_instructions> 丢弃（win 此前整批漏网）");
        Dropped("<environment_context>\ncwd: C:/w/demo\n</environment_context>",
            "W-b: <environment_context> 丢弃（win 此前整批漏网）");

        // ② 带属性的标签形态：必须匹配裸标签名，带 '>' 的老写法在这里全部漏
        Dropped("<system-reminder priority=\"high\">别忘了跑测试</system-reminder>",
            "W-b: <system-reminder 带属性仍丢弃");
        Dropped("<bash-stdout exit=\"0\">From https://x/y</bash-stdout>",
            "W-b: <bash-stdout 带属性仍丢弃");
        Dropped("<bash-stderr code=\"128\">fatal: not a git repo</bash-stderr>",
            "W-b: <bash-stderr 带属性仍丢弃");
        Dropped("<local-command-stdout status=\"ok\">Set model</local-command-stdout>",
            "W-b: <local-command-stdout 带属性仍丢弃");
        Dropped("<local-command-caveat mode=\"x\">本地命令回显</local-command-caveat>",
            "W-b: <local-command-caveat 带属性仍丢弃");
        Dropped("<task-notification id=\"7\"><status>completed</status></task-notification>",
            "W-b: <task-notification 带属性仍丢弃");

        // ③ 正文里长得像标签的内容不能被误伤（原则 2：剥离必须白名单化）
        var kept = new ClaudeParser()
            .ParseLines(ClaudeSessionPath(), new List<RawLine>
            {
                new(0, ClaudeUserLine("把 Option<String> 换成 <port> 占位符")),
            })
            .OfType<UserCommand>().ToList();
        CheckEqual(kept.Count, 1, "W-b: 泛型/占位符正文不被前缀表误伤");
    }

    /// <summary>
    /// W-c：assistant 多 text 段——mac 拼接**全部** type=="text" 段（缺 text 的段跳过），
    /// win 旧实现在**首段**就 break，首段为空/缺 text 时整条结果行凭空消失。
    /// 规范 §1「为数组时取其中 type=="text" 段拼接」以 mac 为准。
    /// </summary>
    private static void ClaudeAssistantMultiSegment()
    {
        string Assistant(string contentJson) =>
            "{\"type\":\"assistant\",\"sessionId\":\"s\",\"timestamp\":\"2026-01-15T10:00:00.000Z\"," +
            "\"cwd\":\"C:/w/demo\",\"message\":{\"role\":\"assistant\",\"content\":" + contentJson + "}}";

        var multi = new ClaudeParser().ParseLines(ClaudeSessionPath(), new List<RawLine>
        {
            new(0, Assistant(
                """[{"type":"text","text":"第一段回复"},{"type":"thinking","thinking":"内心戏"},{"type":"text"},{"type":"text","text":"第二段回复"}]""")),
        }).OfType<TaskComplete>().ToList();
        CheckEqual(multi.Count, 1, "W-c: assistant 多段产出结果行");
        CheckEqual(multi[0].FullText, "第一段回复\n第二段回复",
            "W-c: 全部 text 段以 \\n 拼接（thinking / 缺 text 的段跳过）");

        // 首段为空：旧实现 break 在首段 → IsNullOrWhiteSpace → 整条丢弃
        var emptyFirst = new ClaudeParser().ParseLines(ClaudeSessionPath(), new List<RawLine>
        {
            new(0, Assistant("""[{"type":"text","text":""},{"type":"text","text":"真正的回复在第二段"}]""")),
        }).OfType<TaskComplete>().ToList();
        CheckEqual(emptyFirst.Count, 1, "W-c: 首段为空不再整条丢弃");
        CheckEqual(emptyFirst[0].ResultLine, "真正的回复在第二段", "W-c: 结果行取到后续段落");

        // 一个可用 text 段都没有 → 仍然不产出（与 mac texts.isEmpty → nil 一致）
        var none = new ClaudeParser().ParseLines(ClaudeSessionPath(), new List<RawLine>
        {
            new(0, Assistant("""[{"type":"tool_use","name":"Read"},{"type":"text"}]""")),
        }).ToList();
        CheckEqual(none.Count, 0, "W-c: 无可用 text 段时不产出结果行");
    }

    /// <summary>
    /// W-d：cwd/项目名跨行沿用。claude 不是每行都带 cwd；win 旧实现每行独立回退到
    /// 转义目录 slug（`-Users-x-work-proj`），mac 用 per-file 上下文沿用真实项目叶子名。
    /// </summary>
    private static void ClaudeProjectCarriesAcrossLines()
    {
        var path = ClaudeSessionPath("-Users-x-work-proj");
        var parser = new ClaudeParser();
        var events = parser.ParseLines(path, new List<RawLine>
        {
            new(0, ClaudeUserLine("第一条带 cwd", cwd: "/Users/x/work/proj")),
            new(100, ClaudeUserLine("第二条不带 cwd", cwd: null)),
        }).OfType<UserCommand>().ToList();
        CheckEqual(events.Count, 2, "W-d: 两条命令都产出");
        CheckEqual(events[0].Project, "proj", "W-d: 带 cwd 的行取 cwd 末段");
        CheckEqual(events[1].Project, "proj", "W-d: 无 cwd 的行沿用上下文项目名（不退化成 slug）");

        // 增量续读（下一批 lines，同一 path）仍然沿用
        var later = parser
            .ParseLines(path, new List<RawLine> { new(200, ClaudeUserLine("下一批仍不带 cwd", cwd: null)) })
            .OfType<UserCommand>().ToList();
        CheckEqual(later.Count > 0 ? later[0].Project : "", "proj", "W-d: 跨增量批次沿用项目名");

        // 文件里从头到尾没出现过 cwd → 才回退目录 slug
        var fresh = new ClaudeParser()
            .ParseLines(path, new List<RawLine> { new(0, ClaudeUserLine("从头就没有 cwd", cwd: null)) })
            .OfType<UserCommand>().ToList();
        CheckEqual(fresh.Count > 0 ? fresh[0].Project : "", "-Users-x-work-proj",
            "W-d: 全程无 cwd 才回退目录 slug");
    }

    /// <summary>
    /// 项目名钉在**会话启动目录**：一个会话里的 cwd 会随 subagent / 工具里的 `cd` 漂移，
    /// 旧规则（每条 cwd 都重算项目名）把同一场对话摊到多个"项目"里。
    ///
    /// 实机证据（2026-07-31）：会话 8da61f68 从 `…\hawk-imuikit-aos-agent` 启动，cwd 依次漂到
    /// tools/harness-governance、uikit_uiautomation_midscene、hawk_agent-rs、meeting-hawk、
    /// hawk_server —— 用户 17:06 发的命令被挂到 `meeting-hawk`，在 aos-agent 组里遍寻不着，
    /// 于是把 09:58 那条措辞相近的旧命令当成"自己刚发的、时间显示错了"。
    /// </summary>
    private static void ClaudeProjectPinnedToSessionStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "at-pin-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, ".claude", "projects", "-F--w-repo");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "s.jsonl");
        // 真实文件第一行常常是不带 cwd 的 last-prompt 行——头扫描必须继续往下找。
        var head = "{\"type\":\"last-prompt\",\"sessionId\":\"s\"}";
        var atRoot = ClaudeUserLine("会话在仓库根启动", cwd: "F:/w/repo");
        var drifted = ClaudeUserLine("subagent 把 cwd 带进了子目录", cwd: "F:/w/repo/meeting-hawk");
        var mine = ClaudeUserLine("我在同一个会话里又发了一条", cwd: "F:/w/repo/meeting-hawk");
        File.WriteAllText(path, string.Join("\n", head, atRoot, drifted, mine) + "\n");

        try
        {
            var events = new ClaudeParser().ParseLines(path, new List<RawLine>
            {
                new(0, head), new(40, atRoot), new(200, drifted), new(400, mine),
            }).OfType<UserCommand>().ToList();
            CheckEqual(events.Count, 3, "pin: 三条命令都产出");
            Check(events.All(e => e.Project == "repo"), "pin: cwd 漂到子目录也不改项目名");

            // 断点续读：这一批行全是子目录 cwd，必须回头补读文件头才不会钉错
            var resumed = new ClaudeParser()
                .ParseLines(path, new List<RawLine> { new(400, mine) })
                .OfType<UserCommand>().ToList();
            CheckEqual(resumed.Count > 0 ? resumed[0].Project : "", "repo",
                "pin: 续读补读文件头，不钉在恢复位置所在的子目录");

            CheckEqual(ClaudeParser.FirstCwd(path), "F:/w/repo", "pin: FirstCwd 取文件里第一条 cwd");
            CheckEqual(ClaudeParser.FirstCwd(Path.Combine(dir, "not-here.jsonl")), null,
                "pin: 文件不存在返回 null（不抛）");

            // ---- 存量回填：解析器改规则只管新节点，历史得重算 ----
            var db = TempDbPath();
            try
            {
                using var store = new Store(db);
                var rule = new RuleSummarizer();
                var stale = new UserCommand(AgentKind.Claude, "meeting-hawk", "s",
                    DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "旧节点挂错了组", path, 400);
                var gone = new UserCommand(AgentKind.Claude, "whatever", "s2",
                    DateTimeOffset.FromUnixTimeSeconds(1_700_000_100), "源文件已经没了",
                    Path.Combine(dir, "deleted.jsonl"), 0);
                store.InsertNode(stale, rule.Summarize(stale), SummaryEngine.ComputeHash(stale), false);
                store.InsertNode(gone, rule.Summarize(gone), SummaryEngine.ComputeHash(gone), false);

                CheckEqual(TimelineCoordinator.BackfillProjectPins(store), 1, "pin/回填: 只改了那一行");
                var after = store.GetAllNodesAscending();
                CheckEqual(after.First(n => n.Command.Text == "旧节点挂错了组").Command.Project, "repo",
                    "pin/回填: 存量节点改挂到会话启动目录");
                CheckEqual(after.First(n => n.Command.Text == "源文件已经没了").Command.Project, "whatever",
                    "pin/回填: 源文件不在了就保持原样，不猜");
                CheckEqual(TimelineCoordinator.BackfillProjectPins(store), 0, "pin/回填: 重跑幂等");
            }
            finally { CleanupDb(db); }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// W-e：时间戳缺失/不可解析时的双端共同规则（docs/TEXT-NORMALIZATION.md §4.2 第 14 条）——
    /// ① 形态宽松（.NET TryParse 吃的 ISO 变体照收）；② 解不出 → 沿用**本文件**最后一个
    /// 成功解析的时间戳（确定性、与真实邻居相邻、重扫幂等）；③ 本文件还没有过任何时间戳
    /// → 丢掉该行。
    ///
    /// 旧 win 行为（回退 `UtcNow`）有两处真实危害：节点跳到时间线顶部装成"刚发生"；
    /// ts 参与 `UNIQUE(agent, session_id, ts, command_hash)`，文件重建/重扫时同一条命令
    /// 每次拿新 ts → 唯一键失效 → 重复行。
    ///
    /// ⚠ 载体口径（mac 必须对齐）：进位在**每行解析的最前面**做，任何一行时间戳解析成功
    /// 都会更新回退基准——不限于最终产出事件的行。
    /// </summary>
    private static void TimestampCarryForward()
    {
        var t10 = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

        // ---------- Claude ----------
        var claude = new ClaudeParser().ParseLines(ClaudeSessionPath(), new List<RawLine>
        {
            new(0, ClaudeUserLine("最前面这条没有时间戳", iso: null)),
            new(100, ClaudeUserLine("有正常时间戳的命令")),
            new(200, ClaudeUserLine("后面这条没有时间戳", iso: null)),
            new(300, ClaudeUserLine("后面这条时间戳是坏的", iso: "不是一个时间")),
        }).OfType<UserCommand>().ToList();
        CheckEqual(claude.Count, 3, "W-e/claude: 首个可用时间戳之前的行被丢弃");
        CheckEqual(claude[0].Text, "有正常时间戳的命令", "W-e/claude: 被丢的是最前面那条");
        CheckEqual(claude[0].Timestamp, t10, "W-e/claude: 正常时间戳原样解析");
        CheckEqual(claude[1].Timestamp, t10, "W-e/claude: 缺时间戳的行沿用前一个成功值");
        CheckEqual(claude[2].Timestamp, t10, "W-e/claude: 不可解析的时间戳同样沿用（不回退当前时间）");

        // 进位基准由**任意**行喂养：非事件行（system）也算
        var seeded = new ClaudeParser().ParseLines(ClaudeSessionPath(), new List<RawLine>
        {
            new(0, "{\"type\":\"system\",\"timestamp\":\"2026-01-15T10:00:00.000Z\",\"cwd\":\"C:/w/demo\"}"),
            new(100, ClaudeUserLine("紧跟在 system 行后面、自己没有时间戳", iso: null)),
        }).OfType<UserCommand>().ToList();
        CheckEqual(seeded.Count, 1, "W-e/claude: 非事件行的时间戳也能喂养回退基准");
        CheckEqual(seeded[0].Timestamp, t10, "W-e/claude: 沿用 system 行的时间戳");

        // ---------- Codex ----------
        string CodexMeta(string cwd, string? iso) =>
            "{" + (iso is null ? "" : "\"timestamp\":" + JsonSerializer.Serialize(iso) + ",") +
            "\"type\":\"session_meta\",\"payload\":{\"id\":\"sess-1\",\"cwd\":" +
            JsonSerializer.Serialize(cwd) + "}}";
        string CodexUser(string msg, string? iso) =>
            "{" + (iso is null ? "" : "\"timestamp\":" + JsonSerializer.Serialize(iso) + ",") +
            "\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":" +
            JsonSerializer.Serialize(msg) + "}}";
        const string CodexPath = @"C:\u\.codex\sessions\2026\01\15\rollout-ts.jsonl";

        var codex = new CodexParser().ParseLines(CodexPath, new List<RawLine>
        {
            new(0, CodexMeta("C:/w/demo", iso: null)),
            new(100, CodexUser("最前面这条没有时间戳", iso: null)),
            new(200, CodexUser("有正常时间戳的命令", "2026-01-15T10:00:00.000Z")),
            new(300, CodexUser("后面这条时间戳是坏的", "not-a-time")),
            new(400, "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"办完了\"}}"),
        });
        var codexCmds = codex.OfType<UserCommand>().ToList();
        CheckEqual(codexCmds.Count, 2, "W-e/codex: 首个可用时间戳之前的行被丢弃");
        CheckEqual(codexCmds[0].Timestamp, t10, "W-e/codex: 正常时间戳原样解析");
        CheckEqual(codexCmds[1].Timestamp, t10, "W-e/codex: 坏时间戳沿用前一个成功值");
        var codexDone = codex.OfType<TaskComplete>().ToList();
        CheckEqual(codexDone.Count, 1, "W-e/codex: 缺时间戳的 task_complete 仍产出结果行");
        CheckEqual(codexDone[0].Timestamp, t10, "W-e/codex: 结果行沿用前一个成功值");

        // session_meta 行的时间戳同样喂养回退基准
        var codexSeeded = new CodexParser().ParseLines(
            @"C:\u\.codex\sessions\2026\01\15\rollout-seed.jsonl", new List<RawLine>
            {
                new(0, CodexMeta("C:/w/demo", "2026-01-15T10:00:00.000Z")),
                new(100, CodexUser("首条命令自己没有时间戳", iso: null)),
            }).OfType<UserCommand>().ToList();
        CheckEqual(codexSeeded.Count, 1, "W-e/codex: session_meta 的时间戳也能喂养回退基准");
        CheckEqual(codexSeeded[0].Timestamp, t10, "W-e/codex: 沿用 session_meta 的时间戳");
    }

    /// <summary>
    /// W-a：codex 摘要器自摄取。摘要引擎解析到 `codex exec` 时，CliSummarizer 用
    /// cwd=%LOCALAPPDATA%\AgentTimeline\summarizer 起进程，codex 把每条摘要 prompt 写成
    /// `user_message` 落进 `~\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl`——路径里不含
    /// "AgentTimeline"/"summarizer"，SessionWatcher.ShouldIgnore 的路径级排除够不着。
    /// 判据只能是 `session_meta.payload.cwd`（mac 同判定）。
    /// </summary>
    private static void CodexSummarizerSelfIngestion()
    {
        string Meta(string cwd) =>
            "{\"timestamp\":\"2026-01-15T10:00:00.000Z\",\"type\":\"session_meta\"," +
            "\"payload\":{\"id\":\"sess-self\",\"cwd\":" + JsonSerializer.Serialize(cwd) + "}}";
        string User(string msg) =>
            "{\"timestamp\":\"2026-01-15T10:00:01.000Z\",\"type\":\"event_msg\"," +
            "\"payload\":{\"type\":\"user_message\",\"message\":" + JsonSerializer.Serialize(msg) + "}}";
        const string Done =
            "{\"timestamp\":\"2026-01-15T10:00:02.000Z\",\"type\":\"event_msg\"," +
            "\"payload\":{\"type\":\"task_complete\",\"last_agent_message\":\"{\\\"title\\\":\\\"x\\\"}\"}}";

        var self = new CodexParser().ParseLines(
            @"C:\u\.codex\sessions\2026\01\15\rollout-self.jsonl", new List<RawLine>
            {
                new(0, Meta(AppPaths.SummarizerWorkDir)),
                new(300, User("请为下面这条用户命令产出 JSON 摘要：建立缓存层")),
                new(600, Done),
            });
        CheckEqual(self.Count, 0, "W-a: 摘要器自身 rollout（cwd=scratch）零事件");

        var ok = new CodexParser().ParseLines(
            @"C:\u\.codex\sessions\2026\01\15\rollout-ok.jsonl", new List<RawLine>
            {
                new(0, Meta("C:/w/demo")),
                new(300, User("正常项目里的 codex 命令")),
            }).OfType<UserCommand>().ToList();
        CheckEqual(ok.Count, 1, "W-a: 正常 cwd 的 rollout 照常产出命令");
        CheckEqual(ok[0].Text, "正常项目里的 codex 命令", "W-a: 正常命令正文不受影响");
        CheckEqual(ok[0].Project, "demo", "W-a: 正常 cwd 仍派生项目名");

        // 重启后从持久化 offset 续扫：meta 行在偏移之前，靠 EnsureMeta 首行直读恢复。
        // 自摄取回路恰恰最容易在重启后复现，这条路径必须同样认出 scratch cwd。
        var tmpDir = Path.Combine(Path.GetTempPath(), "at-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var file = Path.Combine(tmpDir, "rollout-resume.jsonl");
            var metaLine = Meta(AppPaths.SummarizerWorkDir);
            var userLine = User("重启后又发出的摘要 prompt");
            File.WriteAllText(file, metaLine + "\n" + userLine + "\n");
            var offset = System.Text.Encoding.UTF8.GetByteCount(metaLine + "\n");
            var resumed = new CodexParser().ParseLines(file, new List<RawLine> { new(offset, userLine) });
            CheckEqual(resumed.Count, 0, "W-a: 重启续扫（EnsureMeta 首行直读）同样认出 scratch cwd");
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
        }

        // claude 通道同判定（mac ClaudeParser 也在 cwd 上认）：SessionWatcher 的路径级
        // 排除对 claude 有效（项目 slug 含 AgentTimeline+summarizer），这里是同源双保险。
        var claudeSelf = new ClaudeParser().ParseLines(
            ClaudeSessionPath("-C--Users-me-AppData-Local-AgentTimeline-summarizer"),
            new List<RawLine> { new(0, ClaudeUserLine("摘要 prompt", cwd: AppPaths.SummarizerWorkDir)) });
        CheckEqual(claudeSelf.Count, 0, "W-a: claude 侧 cwd=scratch 的会话同样零事件");
    }

    /// <summary>
    /// Kimi Code 新布局/新协议（2026-07-28 换代，本机 44 个真实 session 实证）：
    /// `~\.kimi-code\sessions\wd_<项目>_<12hex>\session_<uuid>\agents\main\wire.jsonl`，
    /// 顶层 type：turn.prompt(origin.kind=user) 出命令、
    /// context.append_loop_event/content.part(part.type=text) 出结果行（think 排除）。
    /// 另含：摘要 prompt 输入按 4000 截断（对齐 mac，防长文撑爆上下文）。
    /// </summary>
    private static void KimiWireProtocolAndPromptLimit()
    {
        var parser = new KimiParser();
        var path = Path.Combine(Path.GetTempPath(), ".kimi-code", "sessions",
            "wd_agent-timeline_dd8b1189a258", "session_4d5df5df-3fbf", "agents", "main", "wire.jsonl");

        Check(parser.CanHandle(path), "kimi: CanHandle .kimi-code 下的 wire.jsonl");
        Check(!parser.CanHandle(Path.Combine(Path.GetTempPath(), ".kimi", "sessions", "h", "s", "wire.jsonl")),
            "kimi: 旧 .kimi 布局不再接手");
        // 子 agent 整文件排除（同 Claude 的 isSidechain 语义）：它与 main 共用
        // session_<uuid> 目录名 → 共用 sessionId，其回复会抢占 main 命令节点的结果行
        // （实机审计：本机 67 个子 agent 文件、63 条回复，5 个节点结果行被错配）。
        Check(!parser.CanHandle(Path.Combine(Path.GetTempPath(), ".kimi-code", "sessions",
            "wd_x_0123456789ab", "session_a", "agents", "agent-7", "wire.jsonl")),
            "kimi: 子 agent 的 wire 整文件不接手");
        // 路径形状锚定：形状不符时旧实现会把 sessionId 退化成 ".kimi-code"、project 退化成用户名
        Check(!parser.CanHandle(Path.Combine(Path.GetTempPath(), ".kimi-code", "sessions",
            "wd_x_0123456789ab", "wire.jsonl")),
            "kimi: 缺 session_/agents 层级的浅路径不接手");
        Check(!parser.CanHandle(Path.Combine(Path.GetDirectoryName(path)!, "transcript.jsonl")),
            "kimi: 其他文件名不接手");

        // 项目名去壳：wd_ 前缀 + 末段 hash 剥掉，名字自带的下划线必须保留。
        CheckEqual(KimiParser.ProjectNameFromWorkDir("wd_hawk_agent-rs_dd8b1189a258"), "hawk_agent-rs",
            "kimi: 项目名含下划线时只剥前缀与末段 hash");
        CheckEqual(KimiParser.ProjectNameFromWorkDir("wd_litianyi_e819714055c1"), "litianyi",
            "kimi: 常规工作目录名去壳");
        CheckEqual(KimiParser.ProjectNameFromWorkDir("wd_no-hash-here"), "no-hash-here",
            "kimi: 无 hash 后缀时原样保留");
        CheckEqual(KimiParser.ProjectNameFromWorkDir("something_else"), "something_else",
            "kimi: 不匹配 wd_ 前缀时回退目录名");

        var events = parser.ParseLines(path, new List<RawLine>
        {
            new(0, """{"type":"metadata","protocol_version":"1.3","created_at":1781173231910}"""),
            new(1, """{"type":"turn.prompt","input":[{"type":"text","text":"实现 T5 的缓存层"},{"type":"image","url":"x"},{"type":"text","text":"顺便补测试"}],"origin":{"kind":"user"},"time":1781801007033}"""),
            new(2, """{"type":"context.append_loop_event","event":{"type":"content.part","part":{"type":"think","text":"我先想想缓存怎么做"}},"time":1781801008000}"""),
            new(3, """{"type":"context.append_loop_event","event":{"type":"step.begin","step":1},"time":1781801008500}"""),
            new(4, """{"type":"context.append_loop_event","event":{"type":"content.part","part":{"type":"text","text":"缓存层已实现。\n\n细节见下。"}},"time":1781801009000}"""),
            new(5, """{"type":"turn.prompt","input":[{"type":"text","text":"自动续跑"}],"origin":{"kind":"system_trigger"},"time":1781801010000}"""),
            new(6, """{"type":"context.append_message","message":{"role":"user","content":"注入的上下文"},"time":1781801011000}"""),
            new(7, """{"type":"usage.record","tokens":123,"time":1781801012000}"""),
            new(8, """{"type":"turn.prompt","input":[{"type":"text","text":"/model"}],"origin":{"kind":"user"},"time":1781801013000}"""),
            new(9, """{"type":"turn.prompt","input":[{"type":"text","text":"   "}],"origin":{"kind":"user"},"time":1781801014000}"""),
            new(10, "not json at all"),
        });

        var cmd = events.OfType<UserCommand>().Single();
        CheckEqual(cmd.Text, "实现 T5 的缓存层\n顺便补测试", "kimi: turn.prompt 拼接全部 text 分片（非 text 跳过）");
        CheckEqual(cmd.SessionId, "session_4d5df5df-3fbf", "kimi: sessionId = session_ 目录名");
        CheckEqual(cmd.Project, "agent-timeline", "kimi: project = 工作目录名去壳");
        CheckEqual(cmd.Timestamp.ToUnixTimeMilliseconds(), 1781801007033L, "kimi: time 按毫秒 epoch 解析");
        CheckEqual(cmd.SourceOffset, 1L, "kimi: 行偏移原样带出");

        var done = events.OfType<TaskComplete>().ToList();
        CheckEqual(done.Count, 1, "kimi: 只有 part.type==text 出结果行（think/step 事件排除）");
        CheckEqual(done[0].ResultLine, "缓存层已实现。", "kimi: content.part 文本取首段作结果行");
        CheckEqual(done[0].FullText, "缓存层已实现。\n\n细节见下。", "kimi: 全文保留供代号挖掘");
        CheckEqual(events.Count, 2, "kimi: 非用户 origin / 注入消息 / 无关类型 / 裸斜杠 / 空白 / 坏行全部跳过");

        var longCmd = Cmd(new string('x', 5000), 1_700_000_000);
        var prompt = SummaryJson.BuildPrompt(longCmd);
        Check(prompt.Contains(new string('x', 4000)) && !prompt.Contains(new string('x', 4001)),
            "prompt: 命令原文按 4000 截断");
        // W4：注入 agent 与 project 上下文（同一命令在不同项目里含义不同）
        Check(prompt.Contains("Claude"), "W4: prompt 注入 agent 名");
        Check(prompt.Contains("项目：proj"), "W4: prompt 注入项目名");
        Check(prompt.Contains("用户命令原文："), "W4: 正文骨架与 mac 一致");
    }

    /// <summary>
    /// 结果行不再退化成光秃秃的标题（实机审计：kimi 回复几乎总以 `## Summary` 开头，
    /// 用户库里 7 条结果行字面就是 "Summary"，≤12 字符占比 38.9%）。
    /// </summary>
    private static void ResultExcerptSkipsHeadings()
    {
        CheckEqual(ParserUtil.ResultExcerpt("## Summary\n\nI independently reviewed the draft."),
            "I independently reviewed the draft.", "excerpt: 剥掉 ## 标题取正文段");
        CheckEqual(ParserUtil.ResultExcerpt("# RCA Output — x\n\n## Summary\n\n根因是分区倾斜。"),
            "根因是分区倾斜。", "excerpt: 连续多级标题全部剥掉");
        CheckEqual(ParserUtil.ResultExcerpt("# 只有标题"), "只有标题",
            "excerpt: 全篇皆标题时回退到标题本身（永不空串）");
        CheckEqual(ParserUtil.ResultExcerpt("#include <stdio.h> 已引入\n\n次段"),
            "#include <stdio.h> 已引入", "excerpt: #include 不是标题（需尾随空格）");
        CheckEqual(ParserUtil.ResultExcerpt("正文在前。\n\n## 后面的标题不动"),
            "正文在前。", "excerpt: 只剥开头，正文中的标题不受影响");
    }

    /// <summary>W3 结果行时间戳护栏 + W5 provider 请求构造对齐。</summary>
    private static void ResultLineGuardAndProviderUrl()
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
            var early = Insert("较早的命令", 1_700_000_000);
            var late = Insert("较晚的命令", 1_700_000_900);

            // 回复时间落在两条命令之间 → 只能挂到「早」那条，不能挂到更新的命令上
            var mid = DateTimeOffset.FromUnixTimeSeconds(1_700_000_500);
            CheckEqual(store.SetResultLine(AgentKind.Claude, "s", "早命令的回复", mid), early,
                "W3: 结果行挂到 ts<= 的最新节点（不越到更晚的命令）");
            CheckEqual(store.GetNode(late)?.Summary.ResultLine, null, "W3: 更晚的命令未被污染");

            // 回复时间晚于两条 → 挂到最新那条
            var after = DateTimeOffset.FromUnixTimeSeconds(1_700_001_000);
            CheckEqual(store.SetResultLine(AgentKind.Claude, "s", "晚命令的回复", after), late,
                "W3: 时间戳之后的回复正常挂最新节点");

            // 早于所有节点 → 无处可挂
            var before = DateTimeOffset.FromUnixTimeSeconds(1_699_999_000);
            Check(store.SetResultLine(AgentKind.Claude, "s", "孤儿回复", before) is null,
                "W3: 早于全部节点时不挂载");
        }
        finally
        {
            CleanupDb(dbPath);
        }

        // W5：base URL 自动补 /v1（用户最常见写法是不带 /v1，不补直接 404）
        CheckEqual(ProviderSummarizer.BuildChatCompletionsUrl("https://api.openai.com"),
            "https://api.openai.com/v1/chat/completions", "W5: 无 /v1 时自动补全");
        CheckEqual(ProviderSummarizer.BuildChatCompletionsUrl("https://api.openai.com/v1"),
            "https://api.openai.com/v1/chat/completions", "W5: 已带 /v1 不重复补");
        CheckEqual(ProviderSummarizer.BuildChatCompletionsUrl("https://x.test/v1/ "),
            "https://x.test/v1/chat/completions", "W5: 尾斜杠与空白容错");
    }

    /// <summary>
    /// W0 排队命令补录：mid-turn 被消费的 prompt 只剩 queued_command attachment 一份记录；
    /// 必须复用 L1 忽略前缀（本机语料 217 条中 200 条是注入块）。
    /// </summary>
    private static void ClaudeQueuedCommandRecovery()
    {
        var parser = new ClaudeParser();
        var path = Path.Combine(Path.GetTempPath(), ".claude", "projects", "-demo", "s.jsonl");
        string Att(string prompt, bool sidechain = false, string type = "queued_command") =>
            "{\"type\":\"attachment\",\"timestamp\":\"2026-07-27T10:00:00.000Z\",\"sessionId\":\"s\"," +
            "\"cwd\":\"C:/w/demo\"" + (sidechain ? ",\"isSidechain\":true" : "") +
            ",\"attachment\":{\"type\":\"" + type + "\",\"prompt\":" +
            JsonSerializer.Serialize(prompt) + "}}";

        var events = parser.ParseLines(path, new List<RawLine>
        {
            new(0, Att("排队时键入的真实命令")),
            new(1, Att("<task-notification>\n<task-id>x</task-id>\n</task-notification>")),
            new(2, Att("<local-command-stdout>Set model</local-command-stdout>")),
            new(3, Att("子 agent 的排队命令", sidechain: true)),
            new(4, Att("   ")),
            new(5, Att("其他附件类型的正文", type: "file_snapshot")),
        }).OfType<UserCommand>().ToList();

        CheckEqual(events.Count, 1, "W0: 只补录真实排队命令（注入块/sidechain/空白/他类附件全跳过）");
        CheckEqual(events[0].Text, "排队时键入的真实命令", "W0: prompt 原文入库");
        CheckEqual(events[0].Project, "demo", "W0: 项目名取 cwd 末段");
        CheckEqual(events[0].SessionId, "s", "W0: sessionId 取行内字段");
    }

    /// <summary>
    /// W1 摘要重试上限 + W2 最新优先：attempts 计数、上限过滤、设置保存后清零；
    /// 待办查询按 ts 降序（用户盯着的顶部节点先拿到 LLM 标题）。
    /// </summary>
    private static void SummaryAttemptsAndPriority()
    {
        var dbPath = TempDbPath();
        try
        {
            using var store = new Store(dbPath);
            var rule = new RuleSummarizer();
            long Insert(string text, long ts)
            {
                var c = Cmd(text, ts);
                return store.InsertNode(c, rule.Summarize(c), SummaryEngine.ComputeHash(c), summaryPending: true);
            }
            var older = Insert("较旧的命令", 1_700_000_000);
            var newer = Insert("较新的命令", 1_700_000_500);

            var pending = store.GetPendingSummaries();
            CheckEqual(pending.Count, 2, "W1: 两条 pending 节点都取到");
            CheckEqual(pending[0].Id, newer, "W2: 最新优先（ts 降序）");
            CheckEqual(pending[1].Id, older, "W2: 较旧的排后面");

            CheckEqual(store.BumpSummaryAttempts(older), 1, "W1: attempts 首次 bump = 1");
            CheckEqual(store.BumpSummaryAttempts(older), 2, "W1: 再 bump = 2");
            CheckEqual(store.BumpSummaryAttempts(older), 3, "W1: 第三次 = 3（达上限）");
            CheckEqual(store.GetPendingSummaries().Count, 1, "W1: 达上限的节点被排除出重试集");

            store.ResetSummaryAttempts();
            CheckEqual(store.GetPendingSummaries().Count, 2, "W1: 设置保存清零后重新可试");

            // 摘要落地后不再是 pending，也就不会再被重试拾起
            var c2 = Cmd("较旧的命令", 1_700_000_000);
            store.UpdateSummary(older, rule.Summarize(c2), pending: false);
            CheckEqual(store.GetPendingSummaries().Count, 1, "W1: 摘要成功的节点退出重试集");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    /// <summary>codex 技能调用回显剥本机 SKILL.md 路径,保留 $plugin:skill 徽标文字。</summary>
    private static void CodexParserSkillEcho()
    {
        var parser = new CodexParser();
        string Line(string msg) =>
            "{\"timestamp\":\"2026-07-27T10:00:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":" +
            JsonSerializer.Serialize(msg) + "}}";
        var events = parser.ParseLines(@"C:\u\.codex\sessions\2026\07\27\rollout-x.jsonl", new List<RawLine>
        {
            new(0, Line(@"[$hopper:continue](C:\Users\me\.codex\plugins\cache\hopper\0.1\skills\continue\SKILL.md) 按既定目标推进")),
            new(1, Line("普通 codex 命令")),
            // 编排器包装：壳不是人写的，芯是（本机语料 72 条，此前 37 个节点标题字面是 "<task>"）
            new(2, Line("<task>\n严苛审核路线图文件 04-roadmap.md。\n\n背景：R5 基线 45.9%。\n</task>")),
            // 自动化循环自发的轮次，不是用户命令（含 automation_id/current_time_iso）
            new(3, Line("<heartbeat>\n  <automation_id>f016-coordinator</automation_id>\n</heartbeat>")),
            // 带属性的注入块：裸标签名匹配才挡得住（带 '>' 的前缀匹配不上）
            new(4, Line("<environment_context version=\"2\">\ncwd=F:\\x\n</environment_context>")),
        }).OfType<UserCommand>().ToList();
        CheckEqual(events.Count, 3, "codex: heartbeat 与带属性注入块被跳过");
        CheckEqual(events[0].Text, "$hopper:continue 按既定目标推进", "codex skill echo: 徽标留下路径剥掉");
        CheckEqual(events[1].Text, "普通 codex 命令", "codex skill echo: 普通命令不受影响");
        CheckEqual(events[2].Text, "严苛审核路线图文件 04-roadmap.md。\n\n背景：R5 基线 45.9%。",
            "codex: <task> 去壳留正文");
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

    /// <summary>
    /// 文本规整 golden 基准（docs/normalize-cases.tsv）——双端共享单一事实源，
    /// mac 移植时读同一份文件断言同一批期望值（与 design-tokens.json 同源文化一致）。
    /// 另含幂等断言 normalize(normalize(x)) == normalize(x)（§3.4-3）。
    /// </summary>
    private static void TextNormalizerGoldenCases()
    {
        var tsv = FindRepoFile(Path.Combine("docs", "normalize-cases.tsv"));
        if (tsv is null) { Check(false, "normalize: 找不到 docs/normalize-cases.tsv"); return; }

        var cases = 0;
        foreach (var raw in File.ReadAllLines(tsv))
        {
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            var cols = raw.Split('\t');
            if (cols.Length < 3) continue;
            var id = cols[0];
            var profile = cols[1] switch
            {
                "summary" => NormalizeProfile.Summary,
                "mining" => NormalizeProfile.Mining,
                _ => NormalizeProfile.Excerpt,
            };
            var input = Unescape(cols[2]);
            var expected = Unescape(cols.Length > 3 ? cols[3] : "");

            var actual = TextNormalizer.Normalize(input, profile);
            CheckEqual(actual, expected, $"normalize[{id}]");
            // -noidem 用例的输出是「回填 verbatim」的裸标记，再跑一遍自然会被 unwrap
            if (!id.EndsWith("-noidem", StringComparison.Ordinal))
            {
                CheckEqual(TextNormalizer.Normalize(actual, profile), actual, $"normalize[{id}] 幂等");
            }
            cases++;
        }
        Check(cases >= 40, $"normalize: golden 用例数 {cases} ≥ 40");

        static string Unescape(string s) => s
            .Replace("\\n", "\n").Replace("\\t", "\t")
            .Replace("\\e", "").Replace("\\r", "\r").Replace("\\\\", "\\");
    }

    /// <summary>从当前目录向上找仓库内文件（bin 目录深度不定）。</summary>
    private static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 空串兜底（docs/TEXT-NORMALIZATION.md §3.4-1，审查确认的唯一 UI 可见回归）：
    /// 整段都是围栏/表格时规整后为空 → 回退未规整文本；Store 入口再挡一道。
    /// </summary>
    private static void ResultExcerptFallback()
    {
        var allFence = "```rust\nfn main() {}\n```";
        var excerpt = ParserUtil.ResultExcerpt(allFence);
        Check(excerpt.Length > 0, "fallback: 全文即围栏时不返回空串");
        Check(excerpt.Contains("fn main"), "fallback: 回退到未规整文本");

        var allTable = "| 供应商 | 价格 |\n|---|---|\n| Auth0 | 高 |";
        Check(ParserUtil.ResultExcerpt(allTable).Length > 0, "fallback: 全文即表格时不返回空串");

        var dbPath = TempDbPath();
        try
        {
            using var store = new Store(dbPath);
            var rule = new RuleSummarizer();
            var cmd = Cmd("建立缓存层", 1_700_000_000);
            var id = store.InsertNode(cmd, rule.Summarize(cmd), SummaryEngine.ComputeHash(cmd), false);
            var replyAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_100);
            store.SetResultLine(AgentKind.Claude, "s", "首个结果行", replyAt);
            CheckEqual(store.GetNode(id)?.Summary.ResultLine, "首个结果行", "fallback: 正常结果行写入");
            Check(store.SetResultLine(AgentKind.Claude, "s", "   ", replyAt) is null,
                "fallback: 空白结果行被 Store 挡下");
            CheckEqual(store.GetNode(id)?.Summary.ResultLine, "首个结果行",
                "fallback: 已有结果行不被空串覆盖");
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    /// <summary>结果摘录取首个非空段落（展开态可读全,折叠态由 UI 单行钳制）。</summary>
    private static void ResultExcerptParagraph()
    {
        CheckEqual(ParserUtil.ResultExcerpt("首段第一行\n首段第二行\n\n次段内容"),
            "首段第一行\n首段第二行", "excerpt: 空行分隔取首段(保留段内换行)");
        CheckEqual(ParserUtil.ResultExcerpt("只有一段没有空行\n第二行"),
            "只有一段没有空行\n第二行", "excerpt: 无空行取全文");
        Check(ParserUtil.ResultExcerpt(new string('长', 600)).Length == 501,
            "excerpt: 超长按 500 截断加省略号");
        CheckEqual(ParserUtil.ResultExcerpt("  \n\n正文段  \n\n尾段"),
            "正文段", "excerpt: 首尾空白剥离后取段");
    }

    /// <summary>
    /// §3.3 引子续接：首段以冒号收尾时正文在下一段，只取首段会只剩引子。
    ///
    /// 回归来源：用户库 `TH-0025 是一条安全类 issue,核心是一句话:`——28 字，
    /// 正文（引用块）整段丢失。库内 357 条结果行有 14 条是这个形态。
    /// </summary>
    private static void ResultExcerptLeadIn()
    {
        CheckEqual(
            ParserUtil.ResultExcerpt("TH-0025 是一条**安全**类 issue,核心是一句话:\n\n> **正文在这里。**\n\n## 事实核对"),
            "TH-0025 是一条安全类 issue,核心是一句话: 正文在这里。",
            "excerpt: 引子续接下一段，引用标记剥掉");
        CheckEqual(
            ParserUtil.ResultExcerpt("两个文件已放到桌面：\n\n- 封面.png\n- 成品图.png"),
            "两个文件已放到桌面： 封面.png\n- 成品图.png",
            "excerpt: 列表承接剥首行 '- '");
        CheckEqual(ParserUtil.ResultExcerpt("结论如下：\n\n已收口。"),
            "结论如下： 已收口。", "excerpt: 全角冒号同为引子");
        CheckEqual(ParserUtil.ResultExcerpt("**首段结论**已收口。\n\n第二段细节不应进入结果行。"),
            "首段结论已收口。", "excerpt: 非引子首段不得续接（与修改前逐字节一致）");
        CheckEqual(ParserUtil.ResultExcerpt("分两步:\n\n第一步:\n\n落地完成。"),
            "分两步: 第一步: 落地完成。", "excerpt: 引子链吃到非引子段为止");
        CheckEqual(ParserUtil.ResultExcerpt("汇报:"), "汇报:",
            "excerpt: 引子后无正文时不得产出尾随空格");
    }

    /// <summary>W6：按 grapheme 簇截断——代理对、ZWJ 家庭、组合字、变体选择符都不劈开。</summary>
    private static void ClipSurrogateSafety()
    {
        var atBoundary = new string('a', 19) + "😀";   // 21 code units，😀 跨在截断点上
        CheckEqual(ParserUtil.Clip(atBoundary, 20), new string('a', 19) + "…", "clip: 高代理回退一位");
        CheckEqual(ParserUtil.Clip(new string('b', 25), 20), new string('b', 20) + "…", "clip: 普通截断加省略号");
        CheckEqual(ParserUtil.Clip("short", 20), "short", "clip: 不超长原样返回");

        // ZWJ 家庭 👨‍👩‍👧 是 8 个 code unit 的单簇——旧的代理对判定会从 ZWJ 处切开
        var family = new string('a', 15) + "\U0001F468‍\U0001F469‍\U0001F467";
        var clippedFamily = ParserUtil.Clip(family, 20);
        Check(!clippedFamily.Contains('‍'), "clip: ZWJ 序列不被劈开（无游离连接符）");
        CheckEqual(clippedFamily, new string('a', 15) + "…", "clip: 整簇放不下则整簇不取");

        // 组合字 e + U+0301 = é（2 code unit 单簇）
        var combining = new string('a', 19) + "é";
        Check(!ParserUtil.Clip(combining, 20).EndsWith("e…", StringComparison.Ordinal),
            "clip: 组合字不与其基字分离");

        // 变体选择符 ❤️ = U+2764 + U+FE0F
        var vs = new string('a', 19) + "❤️";
        Check(!ParserUtil.Clip(vs, 20).Contains('❤'), "clip: 变体选择符簇不被劈开");
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
