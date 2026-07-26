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
        var lines = command.Text
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var title = lines.Length > 0 ? lines[0] : command.Text.Trim();
        title = ParserUtil.Clip(StripMarkdownNoise(title), 20); // 代理对安全截断
        if (title.Length == 0) title = "(空命令)";

        var keyPoints = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            var point = StripMarkdownNoise(line);
            if (point.Length == 0) continue;
            keyPoints.Add(ParserUtil.Clip(point, 30));
            if (keyPoints.Count >= 3) break;
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

    private static readonly (NodeKind Kind, string[] Keywords)[] KindRules =
    {
        (NodeKind.Fix, new[] { "修复", "fix", "bug", "报错", "崩溃", "闪退" }),
        (NodeKind.Research, new[] { "调研", "研究", "对比", "评估", "分析一下", "survey" }),
        (NodeKind.Learning, new[] { "学习", "讲解", "解释", "什么是", "怎么理解", "教我" }),
        (NodeKind.Requirement, new[] { "需求", "功能描述", "产品述求", "prd" }),
        (NodeKind.Decision, new[] { "决策", "选型", "定方案", "拍板", "确认方案" }),
        (NodeKind.Task, new[] { "任务", "实现", "开发", "执行", "完成", "部署", "重构" }),
    };

    /// <summary>Keyword fallback until the LLM summary lands (PRD §3.3b 规则引擎兜底).</summary>
    public static string? GuessKind(string text)
    {
        var t = text.ToLowerInvariant();
        foreach (var (kind, keywords) in KindRules)
        {
            if (keywords.Any(t.Contains)) return kind.Label();
        }
        return null;
    }

    private static string StripMarkdownNoise(string line) =>
        line.TrimStart('#', '-', '*', '>', ' ', '\t').Trim();
}
