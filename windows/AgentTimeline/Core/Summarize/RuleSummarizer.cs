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
        title = StripMarkdownNoise(title);
        if (title.Length > 20) title = title[..20] + "…";
        if (title.Length == 0) title = "(空命令)";

        var keyPoints = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            var point = StripMarkdownNoise(line);
            if (point.Length == 0) continue;
            keyPoints.Add(point.Length > 30 ? point[..30] + "…" : point);
            if (keyPoints.Count >= 3) break;
        }

        var codenames = CodenameRegistry.ExtractCandidates(command.Text)
            .Select(name => new CodenameDefinition(name, null))
            .ToList();

        return new Summary(title, keyPoints, codenames, ResultLine: null, Source: SummarySource.Rule);
    }

    private static string StripMarkdownNoise(string line) =>
        line.TrimStart('#', '-', '*', '>', ' ', '\t').Trim();
}
