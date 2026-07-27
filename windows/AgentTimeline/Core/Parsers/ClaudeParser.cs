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

    private static readonly Regex CommandArgsRegex = new(
        @"<command-args>\s*(.*?)\s*</command-args>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    // 非人类输入块整条跳过。实机语料普查(2026-07-27,docs/TEXT-NORMALIZATION.md):
    // <task-notification> 793 次、<local-command-stdout> 96 次(含 ANSI)、Caveat:/
    // [Request interrupted/续传 blob 等此前会以「用户命令」身份泄漏进时间线;
    // 清单对齐 mac 端 AgentSessionParser.ignoredPrefixes 既有语义。
    private static readonly string[] IgnoredPrefixes =
    {
        "<local-command-caveat>",
        "<system-reminder>",
        "<local-command-stdout>",
        "<task-notification>",
        "Caveat:",
        "[Request interrupted",
        "This session is being continued from",
    };

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

        // Local command echoes / harness 注入块 are not user prompts.
        foreach (var prefix in IgnoredPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return null;
        }
        // slash 命令回显块有两种字段顺序(<command-name> 先 / <command-message> 先,
        // 语料 60/171 为后者)——只做 command-name 前缀匹配会整批漏网。统一按命令块
        // 转换:取 "/name",非空 <command-args> 是用户真实输入,拼回正文。
        if (text.StartsWith("<command-name>", StringComparison.Ordinal) ||
            text.StartsWith("<command-message>", StringComparison.Ordinal))
        {
            var m = CommandNameRegex.Match(text);
            if (!m.Success) return null;
            var argsMatch = CommandArgsRegex.Match(text);
            var args = argsMatch.Success ? argsMatch.Groups[1].Value.Trim() : "";
            text = args.Length > 0 ? $"{m.Groups[1].Value} {args}" : m.Groups[1].Value;
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
            ResultLine: ParserUtil.ResultExcerpt(firstText),
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
