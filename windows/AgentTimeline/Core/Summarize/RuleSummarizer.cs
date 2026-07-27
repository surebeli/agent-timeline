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
        title = ParserUtil.Clip(title, 20); // 代理对安全截断
        if (title.Length == 0) title = "(空命令)";

        var keyPoints = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            keyPoints.Add(ParserUtil.Clip(line, 30));
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

    // StripMarkdownNoise 已由 TextNormalizer(Summary 档) 取代并删除：它剥 '-'/'>' 却剥不掉
    // "1. "，且 TrimStart('-') 会吃掉 "--force"/"-> 下一步"（docs/TEXT-NORMALIZATION.md §3.3）。
}
