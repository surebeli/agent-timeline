using System.Text;
using System.Text.Json;

namespace AgentTimeline.Core.Parsers;

/// <summary>
/// Kimi (Kimi Code CLI) sessions — docs/SESSION-FORMATS.md §3.
///
/// Path:   %USERPROFILE%\.kimi\sessions\&lt;project-hash&gt;\&lt;session-uuid&gt;\wire.jsonl
///         (sibling state.json may hold custom_title → used as project/session display name;
///          project-hash → cwd mapping is not public, so fall back to hash[..8])
/// Format: first line {"type":"metadata","protocol_version":...};
///         others {timestamp: unix-seconds(float), message: {type, payload}}.
///
///   - user command:  message.type=="TurnBegin" → concat payload.user_input[] items
///     with type=="text"; short slash commands ("/model", ...) are meta, skipped;
///   - task complete: message.type=="TurnEnd" (if present) — payload shape is not
///     fully specified, so a best-effort string is extracted; otherwise skipped.
/// </summary>
public sealed class KimiParser : IAgentSessionParser
{
    public AgentKind Agent => AgentKind.Kimi;

    /// <summary>Cached project display name per wire.jsonl path.</summary>
    private readonly Dictionary<string, string> _projectNames = new();

    public bool CanHandle(string path) =>
        string.Equals(Path.GetFileName(path), "wire.jsonl", StringComparison.OrdinalIgnoreCase) &&
        path.Contains(Path.Combine(".kimi", "sessions"), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SessionEvent> ParseLines(string path, IReadOnlyList<RawLine> lines)
    {
        var events = new List<SessionEvent>();
        var sessionId = Path.GetFileName(Path.GetDirectoryName(path)) ?? "kimi-session";
        var project = ProjectNameFor(path);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line.Text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                // First line: {"type":"metadata", ...} — no timestamp/message.
                if (root.TryGetProperty("type", out var t) &&
                    t.ValueKind == JsonValueKind.String &&
                    t.GetString() == "metadata")
                {
                    continue;
                }

                if (!root.TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var timestamp = ParseUnixSeconds(root);
                var messageType = GetString(message, "type");
                message.TryGetProperty("payload", out var payload);

                if (messageType == "TurnBegin")
                {
                    var text = ExtractUserInput(payload);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    text = text.Trim();

                    // Short slash commands (e.g. "/model") are meta, not prompts (optional rule).
                    if (text.StartsWith('/') && text.Length <= 24 && !text.Contains(' ')) continue;

                    events.Add(new UserCommand(
                        Agent: AgentKind.Kimi,
                        Project: project,
                        SessionId: sessionId,
                        Timestamp: timestamp,
                        Text: text,
                        SourceFile: path,
                        SourceOffset: line.ByteOffset));
                }
                else if (messageType == "TurnEnd")
                {
                    var resultLine = ExtractBestEffortString(payload);
                    if (!string.IsNullOrWhiteSpace(resultLine))
                    {
                        events.Add(new TaskComplete(
                            Agent: AgentKind.Kimi,
                            SessionId: sessionId,
                            Timestamp: timestamp,
                            ResultLine: ParserUtil.FirstLine(resultLine, 160)));
                    }
                }
            }
            catch (JsonException)
            {
                // Skip malformed line.
            }
        }
        return events;
    }

    private static DateTimeOffset ParseUnixSeconds(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var ts) &&
            ts.ValueKind == JsonValueKind.Number &&
            ts.TryGetDouble(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000.0));
        }
        return DateTimeOffset.UtcNow;
    }

    /// <summary>payload.user_input[]: concat "text" of items with type=="text".</summary>
    private static string ExtractUserInput(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return "";
        if (!payload.TryGetProperty("user_input", out var input) ||
            input.ValueKind != JsonValueKind.Array)
        {
            return "";
        }
        var sb = new StringBuilder();
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (GetString(item, "type") != "text") continue;
            var text = GetString(item, "text");
            if (string.IsNullOrEmpty(text)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }

    /// <summary>TurnEnd payload shape is unspecified in the format doc — probe common keys.</summary>
    private static string? ExtractBestEffortString(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.String) return payload.GetString();
        if (payload.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "last_agent_message", "message", "text", "summary" })
        {
            var value = GetString(payload, key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    /// <summary>
    /// Display project name: state.json custom_title next to wire.jsonl when present,
    /// else the first 8 chars of the project hash directory.
    /// </summary>
    private string ProjectNameFor(string wirePath)
    {
        if (_projectNames.TryGetValue(wirePath, out var cached)) return cached;

        var sessionDir = Path.GetDirectoryName(wirePath);
        var hashDir = sessionDir is null ? null : Path.GetDirectoryName(sessionDir);
        var hash = hashDir is null ? "kimi" : Path.GetFileName(hashDir);
        var name = hash.Length > 8 ? hash[..8] : hash;

        try
        {
            var statePath = sessionDir is null ? null : Path.Combine(sessionDir, "state.json");
            if (statePath is not null && File.Exists(statePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                var title = GetString(doc.RootElement, "custom_title");
                if (!string.IsNullOrWhiteSpace(title)) name = title;
            }
        }
        catch
        {
            // state.json is advisory only.
        }

        _projectNames[wirePath] = name;
        return name;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
