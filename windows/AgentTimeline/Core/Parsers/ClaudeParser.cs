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
        // `!cmd` 直通 shell 的**输出**（实机 W0 验证时发现的新泄漏，本机语料 10 条）：
        // 输入侧是用户真实操作、由下面 BashInputRegex 转换保留，输出侧不是人说的话。
        "<bash-stdout>",
        "<bash-stderr>",
        "Caveat:",
        "[Request interrupted",
        "This session is being continued from",
    };

    /// <summary>`!git pull` 直通 shell：命令本身是用户真实操作，转成 "$ cmd" 保留。</summary>
    private static readonly Regex BashInputRegex = new(
        @"^<bash-input>(.*?)</bash-input>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

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
                else if (type == "attachment")
                {
                    var evt = ParseAttachmentLine(path, root, line.ByteOffset);
                    if (evt is not null) events.Add(evt);
                }
                // system / file-history-snapshot / mode / queue-operation ... → ignored
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
        // `!cmd` 直通 shell 的输入：保留为 "$ cmd"（输出侧已在 IgnoredPrefixes 剥掉）。
        if (text.StartsWith("<bash-input>", StringComparison.Ordinal))
        {
            var bash = BashInputRegex.Match(text);
            if (!bash.Success) return null;
            var cmdText = bash.Groups[1].Value.Trim();
            return cmdText.Length == 0 ? null : BuildCommand(path, root, offset, $"$ {cmdText}");
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

        return BuildCommand(path, root, offset, text);
    }

    /// <summary>会话/项目/时间戳的取法在各入口一致，集中一处。</summary>
    private static UserCommand BuildCommand(string path, JsonElement root, long offset, string text) =>
        new(Agent: AgentKind.Claude,
            Project: ParserUtil.ProjectNameFromCwd(
                GetString(root, "cwd"),
                fallback: Path.GetFileName(Path.GetDirectoryName(path)) ?? "claude"),
            SessionId: GetString(root, "sessionId") ?? Path.GetFileNameWithoutExtension(path),
            Timestamp: ParserUtil.ParseIsoTimestamp(GetString(root, "timestamp")),
            Text: text,
            SourceFile: path,
            SourceOffset: offset);

    /// <summary>
    /// 排队命令补录（W0，对齐 mac ClaudeParser.swift 的 attachment 分支）。
    ///
    /// 一轮跑动中键入的 prompt 可能被 mid-turn 消费、**永不重放为 type=user 行**——
    /// 此时 queued_command attachment 是这条命令的唯一记录，丢掉就违反「你提交过的
    /// 每条命令」。（正常出队的 prompt 会重放成 user 行且不带该 attachment，故本路径
    /// 天然不产生重复；即便重复，nodes 的 UNIQUE(agent,session_id,ts,command_hash)
    /// 也已覆盖。）
    ///
    /// ⚠ 必须复用同一套 L1 忽略前缀：本机语料 217 条 queued_command 里 **200 条是
    /// &lt;task-notification&gt; 等注入块**，不过滤就等于把刚堵掉的 793 次泄漏原路引回。
    /// 净新增的真实用户命令 17 条。
    /// </summary>
    private static UserCommand? ParseAttachmentLine(string path, JsonElement root, long offset)
    {
        if (GetBool(root, "isSidechain")) return null;
        if (!root.TryGetProperty("attachment", out var attachment) ||
            attachment.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (GetString(attachment, "type") != "queued_command") return null;

        var text = GetString(attachment, "prompt");
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        foreach (var prefix in IgnoredPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return null;
        }

        return BuildCommand(path, root, offset, text);
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
