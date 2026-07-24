using System.Text.Json;

namespace AgentTimeline.Core.Summarize;

/// <summary>An LLM-backed (or rule-based) summarizer for one user command.</summary>
public interface ISummarizer
{
    string Name { get; }

    /// <summary>Returns null when the summarizer cannot produce a result (caller falls back).</summary>
    Task<Summary?> SummarizeAsync(UserCommand command, CancellationToken ct);
}

/// <summary>
/// Shared prompt + strict-JSON parsing for the summary contract
/// (docs/ARCHITECTURE.md 摘要 JSON 契约, used verbatim by CLI and provider paths):
///   {"title", "keyPoints"[], "codenames"[{name, definition}], "resultLine"}
/// </summary>
public static class SummaryJson
{
    public static string BuildPrompt(string commandText) =>
        $$"""
        你是一个命令摘要器。下面是用户提交给 AI agent 的一条命令原文。
        请输出严格的 JSON（单个对象，不要 markdown 代码块，不要多余文字），格式：
        {"title":"≤20字标题","keyPoints":["关键点/需求点/任务点，每条≤30字，最多5条"],"codenames":[{"name":"命令中出现的任务/需求代号（如 T-PLUGIN-00）","definition":"该代号在本命令中的含义"}],"resultLine":""}
        没有代号时 codenames 用空数组；resultLine 留空字符串。

        <command>
        {{commandText}}
        </command>
        """;

    /// <summary>
    /// Parses model output into a Summary. Tolerates surrounding prose / ```json fences by
    /// extracting the outermost {...} block. Returns null when no valid object is found.
    /// </summary>
    public static Summary? Parse(string raw, SummarySource source)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var title = GetString(root, "title") ?? "";
            if (title.Length == 0) return null;
            if (title.Length > 40) title = title[..40] + "…";

            var keyPoints = new List<string>();
            if (root.TryGetProperty("keyPoints", out var kp) && kp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in kp.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var point = (item.GetString() ?? "").Trim();
                    if (point.Length == 0) continue;
                    keyPoints.Add(point.Length > 60 ? point[..60] + "…" : point);
                    if (keyPoints.Count >= 5) break;
                }
            }

            var codenames = new List<CodenameDefinition>();
            if (root.TryGetProperty("codenames", out var cn) && cn.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cn.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = (GetString(item, "name") ?? "").Trim();
                    if (name.Length == 0) continue;
                    codenames.Add(new CodenameDefinition(name, GetString(item, "definition")));
                }
            }

            var resultLine = GetString(root, "resultLine");
            if (string.IsNullOrWhiteSpace(resultLine)) resultLine = null;

            return new Summary(title, keyPoints, codenames, resultLine, source);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
