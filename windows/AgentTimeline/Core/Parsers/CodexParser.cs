using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentTimeline.Core.Parsers;

/// <summary>
/// Codex sessions — docs/SESSION-FORMATS.md §2.
///
/// Path:   %USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-&lt;ts&gt;-&lt;uuid&gt;.jsonl
/// Format: each line {timestamp, type, payload}; type ∈ session_meta / event_msg /
///         response_item / turn_context / compacted.
///
///   - session_meta:  payload.id (session id), payload.cwd, payload.cli_version;
///   - user command:  type=="event_msg" &amp;&amp; payload.type=="user_message" → payload.message,
///     skipping environment injections starting with &lt;user_instructions&gt; or &lt;environment_context&gt;;
///   - task complete: payload.type=="task_complete" → payload.last_agent_message.
///
/// session_meta is the FIRST line of a rollout file. When tailing resumes mid-file after an
/// app restart the meta line is before our offset, so EnsureMeta re-reads just the first line.
/// </summary>
public sealed class CodexParser : IAgentSessionParser
{
    public AgentKind Agent => AgentKind.Codex;

    /// <summary>[$plugin:skill](…SKILL.md) 技能调用回显 → 只留 $plugin:skill 徽标文字。</summary>
    private static readonly Regex SkillEchoRegex = new(
        @"^\[(\$[^\]\n]+)\]\([^)\n]*SKILL\.md\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private sealed class FileContext
    {
        public string? SessionId;
        public string? Cwd;
        public bool MetaChecked;
    }

    private readonly Dictionary<string, FileContext> _contexts = new();

    public bool CanHandle(string path) =>
        path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(path).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) &&
        path.Contains(Path.Combine(".codex", "sessions"), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SessionEvent> ParseLines(string path, IReadOnlyList<RawLine> lines)
    {
        var events = new List<SessionEvent>();
        if (lines.Count == 0) return events;

        if (!_contexts.TryGetValue(path, out var ctx))
        {
            ctx = new FileContext();
            _contexts[path] = ctx;
        }
        // Resuming mid-file without meta? Recover session_meta from line 0.
        if (ctx.SessionId is null && !ctx.MetaChecked && lines[0].ByteOffset > 0)
        {
            EnsureMeta(path, ctx);
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line.Text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var type = GetString(root, "type");
                var timestamp = ParserUtil.ParseIsoTimestamp(GetString(root, "timestamp"));
                if (!root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                switch (type)
                {
                    case "session_meta":
                        ctx.SessionId = GetString(payload, "id");
                        ctx.Cwd = GetString(payload, "cwd");
                        ctx.MetaChecked = true;
                        break;

                    case "event_msg":
                        var payloadType = GetString(payload, "type");
                        if (payloadType == "user_message")
                        {
                            var message = GetString(payload, "message");
                            if (string.IsNullOrWhiteSpace(message)) break;
                            var text = message.Trim();
                            // Environment injections, not typed by the user.
                            if (text.StartsWith("<user_instructions>", StringComparison.Ordinal)) break;
                            if (text.StartsWith("<environment_context>", StringComparison.Ordinal)) break;
                            // 插件技能调用回显 [$plugin:skill](本地 SKILL.md 绝对路径) 开头
                            // (语料 17/70 条):保留命令徽标文字,剥掉本机路径(跨机无效
                            // 且泄漏用户名)。见 docs/TEXT-NORMALIZATION.md。
                            text = SkillEchoRegex.Replace(text, "$1", 1).Trim();

                            events.Add(new UserCommand(
                                Agent: AgentKind.Codex,
                                Project: ParserUtil.ProjectNameFromCwd(ctx.Cwd, fallback: "codex"),
                                SessionId: SessionIdFor(path, ctx),
                                Timestamp: timestamp,
                                Text: text,
                                SourceFile: path,
                                SourceOffset: line.ByteOffset));
                        }
                        else if (payloadType == "task_complete")
                        {
                            var last = GetString(payload, "last_agent_message");
                            if (!string.IsNullOrWhiteSpace(last))
                            {
                                events.Add(new TaskComplete(
                                    Agent: AgentKind.Codex,
                                    SessionId: SessionIdFor(path, ctx),
                                    Timestamp: timestamp,
                                    ResultLine: ParserUtil.ResultExcerpt(last),
                                    FullText: last)); // untruncated — mined for codenames
                            }
                        }
                        break;

                    // response_item / turn_context / compacted → agent internals, ignored.
                }
            }
            catch (JsonException)
            {
                // Skip malformed line.
            }
        }
        return events;
    }

    private static string SessionIdFor(string path, FileContext ctx) =>
        ctx.SessionId ?? Path.GetFileNameWithoutExtension(path);

    private void EnsureMeta(string path, FileContext ctx)
    {
        ctx.MetaChecked = true;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var first = reader.ReadLine();
            if (first is null) return;
            using var doc = JsonDocument.Parse(first);
            var root = doc.RootElement;
            if (GetString(root, "type") == "session_meta" &&
                root.TryGetProperty("payload", out var payload))
            {
                ctx.SessionId = GetString(payload, "id");
                ctx.Cwd = GetString(payload, "cwd");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"CodexParser: failed to recover session_meta from {path}: {ex.Message}");
        }
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
