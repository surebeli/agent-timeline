using System.Text.Json;
using AgentTimeline.Core.Parsers;

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
    /// <summary>
    /// 进 prompt 的命令原文上限（对齐 mac SummaryPrompt 的 4000）：粘贴长文/派发式
    /// prompt 动辄数万字符，不截断会把 CLI 与 provider 的上下文撑爆且拖慢摘要。
    /// </summary>
    private const int PromptInputLimit = 4000;

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
        {{ParserUtil.Clip(commandText, PromptInputLimit)}}
        </command>
        """;

    /// <summary>
    /// Parses model output into a Summary. Tolerates surrounding prose / ```json fences:
    /// 逐个枚举平衡的 {...} 候选块（引号/转义感知），**从后往前**尝试解析——codex exec
    /// 的 stdout 混有 workdir/统计等前导杂讯，正文闲聊里出现花括号会把「最外层
    /// 首 { 到末 }」的旧提取法带进无关文本（M3 审计发现）；摘要 JSON 通常在输出
    /// 末尾，后向优先命中率最高。Returns null when no candidate parses.
    /// </summary>
    public static Summary? Parse(string raw, SummarySource source)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var candidate in JsonObjectCandidates(raw))
        {
            var summary = ParseObject(candidate, source);
            if (summary is not null) return summary;
        }
        return null;
    }

    /// <summary>顶层平衡 {...} 子串，后出现的先返回；字符串内的花括号不参与配平。</summary>
    private static IEnumerable<string> JsonObjectCandidates(string raw)
    {
        var candidates = new List<string>();
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"' when depth > 0:
                    inString = true;
                    break;
                case '{':
                    if (depth == 0) start = i;
                    depth++;
                    break;
                case '}':
                    if (depth > 0 && --depth == 0 && start >= 0)
                    {
                        candidates.Add(raw[start..(i + 1)]);
                        start = -1;
                        if (candidates.Count >= 16) i = raw.Length; // 防病态输入
                    }
                    break;
            }
        }
        candidates.Reverse();
        return candidates;
    }

    private static Summary? ParseObject(string json, SummarySource source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var title = GetString(root, "title") ?? "";
            if (title.Length == 0) return null;
            if (title.Length > 40) title = ParserUtil.Clip(title, 40);

            var keyPoints = new List<string>();
            if (root.TryGetProperty("keyPoints", out var kp) && kp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in kp.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var point = (item.GetString() ?? "").Trim();
                    if (point.Length == 0) continue;
                    keyPoints.Add(ParserUtil.Clip(point, 60));
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
                    if (definition is not null) definition = ParserUtil.Clip(definition, 60);
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
