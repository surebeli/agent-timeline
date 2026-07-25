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
/// Shared prompt + strict-JSON parsing for the summary contract, mirroring macos
/// SummaryPrompt (lifecycle revision):
///   {"title", "kind", "keyPoints"[], "codenames"[{name, definition, status}], "resultLine"}
/// kind ∈ 需求|任务|调研|学习|决策|修复|其他; codename status ∈ 定义|进行中|完成|变更|提及.
/// </summary>
public static class SummaryJson
{
    public static string BuildPrompt(string commandText) =>
        $$"""
        你是一个命令摘要器。下面是用户提交给 AI agent 的一条命令原文。请只输出一个 JSON 对象（不要 markdown 代码块、不要任何解释），字段如下：
        {"title": "≤20字的标题，概括这条命令要做什么",
         "kind": "按命令主要意图归类，取 需求|任务|调研|学习|决策|修复|其他 之一",
         "keyPoints": ["关键点/需求点/任务点，每条≤30字，最多5条；命令简单时可为空数组"],
         "codenames": [{"name": "命令中出现的需求/任务/里程碑代号，短码（N1、T2、M1）和长码（REQ-3、T-PLUGIN-00）都算；没有则空数组",
                        "definition": "该代号指代的具体内容，≤40字；若本命令只是提及或更新状态而没有给出定义，留空字符串",
                        "status": "该代号在本命令中的生命周期信号，取 定义|进行中|完成|变更|提及 之一"}],
         "resultLine": null}

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
                    // Plausibility gate: models occasionally emit list indices ("1"),
                    // punctuation, or tech vocabulary (S3/Q1) as "codenames".
                    if (!CodenameDetector.IsPlausibleName(name)) continue;
                    var definition = GetString(item, "definition");
                    if (definition is { Length: > 60 }) definition = definition[..60] + "…";
                    // Status label passes through raw; CodenameRegistry validates on use.
                    codenames.Add(new CodenameDefinition(name, definition, GetString(item, "status")));
                }
            }

            var resultLine = GetString(root, "resultLine");
            if (string.IsNullOrWhiteSpace(resultLine)) resultLine = null;

            // Kind must be one of the NodeKind labels; anything else is dropped.
            var kind = NodeKinds.Normalize(GetString(root, "kind"));

            return new Summary(title, keyPoints, codenames, resultLine, source, kind);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
