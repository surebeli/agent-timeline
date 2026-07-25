using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentTimeline.Core.Parsers;

/// <summary>
/// Claude Code sessions — docs/SESSION-FORMATS.md §1.
///
/// Path:   %USERPROFILE%\.claude\projects\&lt;project-slug&gt;\&lt;session-uuid&gt;.jsonl
/// Format: one JSON object per line, discriminated by "type".
///
/// User command extraction (type == "user"):
///   - skip isMeta == true;
///   - skip isSidechain == true (sub-agent conversations);
///   - message.content: string → as-is; array → concat segments with type=="text",
///     SKIP "tool_result" segments (tool responses, not user input);
///   - skip content starting with &lt;local-command-caveat&gt; / &lt;system-reminder&gt;
///     (local command echo, not a user prompt); &lt;command-name&gt;/xxx&lt;/command-name&gt;
///     is extracted as a "/xxx" slash-command node (optional rule — implemented);
///   - fields: timestamp (ISO8601), cwd, gitBranch, sessionId, uuid, version.
///
/// Result line: first text segment of each type=="assistant" message is emitted as a
/// TaskComplete candidate; the store keeps the LATEST one per session, which converges
/// to "最后一条 assistant 消息" required by the spec.
/// </summary>
public sealed partial class ClaudeParser : IAgentSessionParser
{
    public AgentKind Agent => AgentKind.Claude;

    private static readonly Regex CommandNameRegex = new(
        @"<command-name>\s*(/[^<\s]+)\s*</command-name>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool CanHandle(string path) =>
        path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
        path.Contains(Path.Combine(".claude", "projects"), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SessionEvent> ParseLines(string path, IReadOnlyList<RawLine> lines)
    {
        var events = new List<SessionEvent>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line.Text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var type = GetString(root, "type");

                if (type == "user")
                {
                    var evt = ParseUserLine(path, root, line.ByteOffset);
                    if (evt is not null) events.Add(evt);
                }
                else if (type == "assistant")
                {
                    var evt = ParseAssistantLine(path, root);
                    if (evt is not null) events.Add(evt);
                }
                // attachment / system / file-history-snapshot / mode / queue-operation ... → ignored
            }
            catch (JsonException)
            {
                // Malformed / truncated line: skip. (Half lines are already retained by the tailer.)
            }
        }
        return events;
    }

    private UserCommand? ParseUserLine(string path, JsonElement root, long offset)
    {
        if (GetBool(root, "isMeta") || GetBool(root, "isSidechain")) return null;
        if (!root.TryGetProperty("message", out var message)) return null;

        var text = ExtractContent(message);
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        // Local command echoes are not user prompts.
        if (text.StartsWith("<local-command-caveat>", StringComparison.Ordinal)) return null;
        if (text.StartsWith("<system-reminder>", StringComparison.Ordinal)) return null;
        if (text.StartsWith("<command-name>", StringComparison.Ordinal))
        {
            // Optional rule: surface "/xxx" as a slash-command node.
            var m = CommandNameRegex.Match(text);
            if (!m.Success) return null;
            text = m.Groups[1].Value;
        }

        var sessionId = GetString(root, "sessionId") ?? Path.GetFileNameWithoutExtension(path);
        var project = ParserUtil.ProjectNameFromCwd(
            GetString(root, "cwd"),
            fallback: Path.GetFileName(Path.GetDirectoryName(path)) ?? "claude");

        return new UserCommand(
            Agent: AgentKind.Claude,
            Project: project,
            SessionId: sessionId,
            Timestamp: ParserUtil.ParseIsoTimestamp(GetString(root, "timestamp")),
            Text: text,
            SourceFile: path,
            SourceOffset: offset);
    }

    private static TaskComplete? ParseAssistantLine(string path, JsonElement root)
    {
        if (GetBool(root, "isSidechain")) return null;
        if (!root.TryGetProperty("message", out var message)) return null;
        if (!message.TryGetProperty("content", out var content)) return null;

        string? firstText = null;
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in content.EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Object &&
                    GetString(segment, "type") == "text")
                {
                    firstText = GetString(segment, "text");
                    break;
                }
            }
        }
        else if (content.ValueKind == JsonValueKind.String)
        {
            firstText = content.GetString();
        }
        if (string.IsNullOrWhiteSpace(firstText)) return null;

        var sessionId = GetString(root, "sessionId") ?? Path.GetFileNameWithoutExtension(path);
        return new TaskComplete(
            Agent: AgentKind.Claude,
            SessionId: sessionId,
            Timestamp: ParserUtil.ParseIsoTimestamp(GetString(root, "timestamp")),
            ResultLine: ParserUtil.FirstLine(firstText, 160),
            FullText: firstText); // untruncated — the coordinator mines it for codenames
    }

    /// <summary>message.content: string, or array of segments (take "text", skip "tool_result").</summary>
    private static string ExtractContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";

        var sb = new StringBuilder();
        foreach (var segment in content.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object) continue;
            var segType = GetString(segment, "type");
            if (segType == "text")
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(GetString(segment, "text") ?? "");
            }
            // "tool_result" and anything else: skipped — tool responses are not user input.
        }
        return sb.ToString();
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}
