using AgentTimeline.Core.Parsers;

namespace AgentTimeline.Core.Summarize;

/// <summary>
/// Zero-dependency fallback (PRD F4.3): first line truncated as title, following lines as
/// key points, codenames from the registry regex. Always succeeds — it is both the instant
/// on-screen summary (before the LLM one lands) and the terminal fallback of the chain.
/// </summary>
public sealed class RuleSummarizer : ISummarizer
{
    public string Name => "rule";

    public Task<Summary?> SummarizeAsync(UserCommand command, CancellationToken ct) =>
        Task.FromResult<Summary?>(Summarize(command));

    public Summary Summarize(UserCommand command)
    {
        // 展示文本走 Summary 档规整（docs/TEXT-NORMALIZATION.md §3.1）：markdown 标记
        // unwrap、行首列表/引用前缀剥除（UI 已有 · 前缀）；围栏只保护不删除——命令侧
        // 删围栏会把用户贴的 spec 整段清空（语料实测 275 条损失 >50% 字符）。
        // 代号检测仍吃 command.Text 原文（下方 DetectDefinitions/Detect），不受影响。
        var display = Text.TextNormalizer.Normalize(command.Text, Text.NormalizeProfile.Summary);
        var lines = display
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var title = lines.Length > 0 ? lines[0] : display.Trim();
        title = ParserUtil.Clip(title, DisplayLimits.SummaryTitle); // 代理对安全截断
        if (title.Length == 0) title = "(空命令)";

        var keyPoints = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            keyPoints.Add(ParserUtil.Clip(line, DisplayLimits.KeyPoint));
            if (keyPoints.Count >= DisplayLimits.KeyPointCount) break;
        }

        // Definition-pattern hits carry their definition + a 定义 status; dash-style
        // candidates that were not defined in this text follow bare (mirrors macos).
        var codenames = CodenameDetector.DetectDefinitions(command.Text)
            .Select(d => new CodenameDefinition(d.Name, d.Definition, CodenameStatus.Defined.Label()))
            .ToList();
        var defined = new HashSet<string>(codenames.Select(c => c.Name), StringComparer.Ordinal);
        codenames.AddRange(CodenameDetector.Detect(command.Text)
            .Where(name => !defined.Contains(name))
            .Select(name => new CodenameDefinition(name, null)));

        return new Summary(title, keyPoints, codenames, ResultLine: null,
            Source: SummarySource.Rule, Kind: GuessKind(command.Text));
    }

    /// <summary>
    /// 类型识别词表，四语常开。与 <see cref="CodenameDetector"/> 的状态词表同理：
    /// 这是**识别**词不是展示文案，不进 <c>design/strings.json</c>——会话里出现哪种语言
    /// 与界面语言无关，四张表必须同时生效。mac 端 RuleSummarizer 镜像同一份表。
    ///
    /// 顺序即优先级（先命中先返回），Fix 在最前：「バグを修正して実装」优先记成修复。
    /// 拉丁词全部走词边界匹配，否则 <c>prefix</c>/<c>suffix</c> 会把一切命令判成 Fix。
    ///
    /// ⚠ **中日同形词是这张表最大的坑**：日语词若与简体中文写法完全相同，就等于同时
    /// 加进了中文表。5189 条真实命令上量过，以下几个必须**排除在外**——
    /// · <c>要求</c>（本想给 Requirement）：中文里是高频通用动词（"按要求执行"），
    ///   而 Requirement 判在 Task 之前，实测把 31 条任务误判成需求；
    /// · <c>判断</c>（本想给 Decision）：同理，中文"判断一下"随处可见。
    /// 日语侧改用无同形碰撞的 <c>要件/仕様</c>、<c>決定/選定/方針</c>，覆盖不受影响。
    /// 反过来 <c>調査/検討/説明/実装/対応</c> 这些用的是日本新字体，简体中文写法不同，安全。
    /// </summary>
    private static readonly (NodeKind Kind, string[] Keywords)[] KindRules =
    {
        (NodeKind.Fix, new[]
        {
            "修复", "报错", "崩溃", "闪退",                                  // zh
            "fix", "bug", "debug", "crash", "regression",                            // en
            "修正", "不具合", "バグ", "エラー", "クラッシュ", "障害",          // ja
            "수정", "버그", "오류", "에러", "크래시", "장애",                 // ko
        }),
        (NodeKind.Research, new[]
        {
            "调研", "研究", "对比", "评估", "分析一下",                       // zh
            "survey", "research", "investigate", "benchmark",               // en
            "調査", "検討", "比較", "評価", "リサーチ",                       // ja
            "조사", "검토", "비교", "평가", "리서치",                         // ko
        }),
        (NodeKind.Learning, new[]
        {
            "学习", "讲解", "解释", "什么是", "怎么理解", "教我",              // zh
            "explain", "tutorial", "what is", "how does",                   // en
            "説明", "解説", "教えて", "とは何", "学習",                       // ja
            "설명", "알려줘", "무엇인가", "배우",                             // ko
        }),
        (NodeKind.Requirement, new[]
        {
            "需求", "功能描述", "产品述求",                                   // zh
            "prd", "requirement", "spec", "specification", "user story",    // en
            "要件", "仕様",                                          // ja
            "요구사항", "요건", "사양", "스펙",                               // ko
        }),
        (NodeKind.Decision, new[]
        {
            "决策", "选型", "定方案", "拍板", "确认方案",                      // zh
            "decision", "decide", "tradeoff", "trade-off",                  // en
            "決定", "選定", "方針",                                  // ja
            "결정", "선정", "판단", "방침",                                  // ko
        }),
        (NodeKind.Task, new[]
        {
            "任务", "实现", "开发", "执行", "完成", "部署", "重构",            // zh
            "implement", "deploy", "refactor",                              // en
            "実装", "開発", "デプロイ", "リファクタ", "対応", "タスク",         // ja
            "구현", "개발", "배포", "리팩터", "작업", "태스크",                // ko
        }),
    };

    /// <summary>Keyword fallback until the LLM summary lands (PRD §3.3b 规则引擎兜底).</summary>
    public static string? GuessKind(string text)
    {
        var t = Text.TextNormalizer.ForMatch(text);
        foreach (var (kind, keywords) in KindRules)
        {
            if (keywords.Any(k => Text.TextNormalizer.ContainsKeyword(t, k))) return kind.Label();
        }
        return null;
    }

    // StripMarkdownNoise 已由 TextNormalizer(Summary 档) 取代并删除：它剥 '-'/'>' 却剥不掉
    // "1. "，且 TrimStart('-') 会吃掉 "--force"/"-> 下一步"（docs/TEXT-NORMALIZATION.md §3.3）。
}
