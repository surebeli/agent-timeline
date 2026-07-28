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
/// Result line: ALL text segments of each type=="assistant" message (joined with "\n")
/// are emitted as a TaskComplete candidate; the store keeps the LATEST one per session,
/// which converges to "最后一条 assistant 消息" required by the spec.
///
/// Per-file context (mirrors mac `ParsedFileContext`): cwd/项目名跨行沿用、本文件最后一个
/// 可解析时间戳（缺失时间戳的行沿用它）、以及"整文件禁用"标记（我们自己摘要器的会话）。
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
    // [Request interrupted/续传 blob 等此前会以「用户命令」身份泄漏进时间线。
    //
    // ⚠ 清单与 mac `ParserSupport.ignoredPrefixes` **逐字一致**（11 条、顺序相同）：
    //   ① 标签一律写**裸标签名、不带闭合 '>'**——harness 会给注入块带属性
    //      （`<system-reminder priority="high">`、`<bash-stdout exit="0">`），
    //      带 '>' 的前缀匹配不上，整块 XML 会变成垃圾"用户命令"节点；
    //   ② `<user_instructions>` / `<environment_context>` 是 Claude 侧也会出现的
    //      环境注入（codex 通道已过滤，claude 通道此前整批漏网）。
    private static readonly string[] IgnoredPrefixes =
    {
        "<local-command-caveat", "<local-command-stdout",
        "<system-reminder", "<user_instructions", "<environment_context", "<task-notification",
        // `!cmd` 直通 shell 的**输出**（实机 W0 验证时发现的新泄漏，本机语料 10 条）：
        // 输入侧是用户真实操作、由下面 BashInputRegex 转换保留，输出侧不是人说的话。
        "<bash-stdout", "<bash-stderr",
        "Caveat:", "[Request interrupted",
        "This session is being continued from",  // post-compaction continuation blob
    };

    /// <summary>`!git pull` 直通 shell：命令本身是用户真实操作，转成 "$ cmd" 保留。</summary>
    private static readonly Regex BashInputRegex = new(
        @"^<bash-input>(.*?)</bash-input>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>
    /// 每文件可变解析状态（对齐 mac `ParsedFileContext`），跨增量读取保留：
    /// 早出现的元信息（cwd/项目名、最后一个可解析时间戳）要对之后的行继续生效。
    /// </summary>
    private sealed class FileContext
    {
        /// <summary>最近一行带的 cwd；无 cwd 的行沿用它（W-d）。</summary>
        public string? Cwd;

        /// <summary>由 <see cref="Cwd"/> 派生的项目显示名。</summary>
        public string? Project;

        /// <summary>本文件里最后一个**成功解析**的时间戳（W-e 回退基准）。</summary>
        public DateTimeOffset? LastTimestamp;

        /// <summary>整文件忽略（我们自己摘要器的 headless 会话）。</summary>
        public bool Disabled;
    }

    private readonly Dictionary<string, FileContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    public bool CanHandle(string path) =>
        path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
        path.Contains(Path.Combine(".claude", "projects"), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SessionEvent> ParseLines(string path, IReadOnlyList<RawLine> lines)
    {
        var events = new List<SessionEvent>();
        if (!_contexts.TryGetValue(path, out var ctx))
        {
            ctx = new FileContext();
            _contexts[path] = ctx;
        }
        if (ctx.Disabled) return events;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line.Text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                // ── cwd / 项目名跨行沿用（W-d，对齐 mac ClaudeParser.parse 开头）──
                // 不是每行都带 cwd；无 cwd 的行若各自独立回退，就会显示转义目录 slug
                // （`-Users-x-work-proj`）而不是项目叶子名。
                var cwd = GetString(root, "cwd");
                if (!string.IsNullOrEmpty(cwd) && !string.Equals(cwd, ctx.Cwd, StringComparison.Ordinal))
                {
                    ctx.Cwd = cwd;
                    ctx.Project = ParserUtil.ProjectNameFromCwd(cwd, fallback: FallbackProject(path));
                    // 我们自己摘要器的 headless 会话永不上时间线（mac 同判定）。
                    if (AppPaths.IsSummarizerWorkDir(cwd)) ctx.Disabled = true;
                }
                if (ctx.Disabled) break;

                // ── 时间戳（W-e，双端共同规则）──
                // 形态宽松解析；解不出就沿用本文件最后一个成功解析的时间戳（确定性、
                // 与真实邻居相邻、重扫幂等）；本文件还没有过任何时间戳则丢掉这一行。
                var parsed = ParserUtil.TryParseIsoTimestamp(GetString(root, "timestamp"));
                if (parsed is not null) ctx.LastTimestamp = parsed;
                if (ctx.LastTimestamp is not { } ts) continue;

                var type = GetString(root, "type");
                if (type == "user")
                {
                    var evt = ParseUserLine(path, root, ctx, ts, line.ByteOffset);
                    if (evt is not null) events.Add(evt);
                }
                else if (type == "assistant")
                {
                    var evt = ParseAssistantLine(path, root, ts);
                    if (evt is not null) events.Add(evt);
                }
                else if (type == "attachment")
                {
                    var evt = ParseAttachmentLine(path, root, ctx, ts, line.ByteOffset);
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

    private static UserCommand? ParseUserLine(
        string path, JsonElement root, FileContext ctx, DateTimeOffset ts, long offset)
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
            return cmdText.Length == 0 ? null : BuildCommand(path, root, ctx, ts, offset, $"$ {cmdText}");
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

        return BuildCommand(path, root, ctx, ts, offset, text);
    }

    /// <summary>会话/项目/时间戳的取法在各入口一致，集中一处。</summary>
    private static UserCommand BuildCommand(
        string path, JsonElement root, FileContext ctx, DateTimeOffset ts, long offset, string text) =>
        new(Agent: AgentKind.Claude,
            Project: ctx.Project ?? FallbackProject(path),
            SessionId: GetString(root, "sessionId") ?? Path.GetFileNameWithoutExtension(path),
            Timestamp: ts,
            Text: text,
            SourceFile: path,
            SourceOffset: offset);

    /// <summary>项目名兜底：文件所在目录名（cwd 的转义 slug）。</summary>
    private static string FallbackProject(string path) =>
        Path.GetFileName(Path.GetDirectoryName(path)) ?? "claude";

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
    private static UserCommand? ParseAttachmentLine(
        string path, JsonElement root, FileContext ctx, DateTimeOffset ts, long offset)
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

        return BuildCommand(path, root, ctx, ts, offset, text);
    }

    private static TaskComplete? ParseAssistantLine(string path, JsonElement root, DateTimeOffset ts)
    {
        if (GetBool(root, "isSidechain")) return null;
        if (!root.TryGetProperty("message", out var message)) return null;

        // 与用户通道同一套抽取（W-c）：**拼接全部** type=="text" 段，不是只取首段。
        // 只取首段时，首段为空/缺 text 的分段回复会整条丢掉结果行。
        var text = ExtractContent(message);
        if (string.IsNullOrWhiteSpace(text)) return null;

        var sessionId = GetString(root, "sessionId") ?? Path.GetFileNameWithoutExtension(path);
        return new TaskComplete(
            Agent: AgentKind.Claude,
            SessionId: sessionId,
            Timestamp: ts,
            ResultLine: ParserUtil.ResultExcerpt(text),
            FullText: text); // untruncated — the coordinator mines it for codenames
    }

    /// <summary>
    /// message.content：string 直接取；数组则把全部 type=="text" 段以 "\n" 拼接
    /// （docs/SESSION-FORMATS.md §1「为数组时取其中 type=="text" 段拼接」）。
    /// "tool_result" 等其他段跳过——那是工具回包不是人的输入。
    /// 段内 text 缺失/非字符串时**跳过该段**（不产生空行），与 mac `compactMap` 同语义；
    /// 空字符串是合法内容，保留（拼接后表现为空行）。
    /// </summary>
    private static string ExtractContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";

        var texts = new List<string>();
        foreach (var segment in content.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object) continue;
            if (GetString(segment, "type") != "text") continue;
            if (GetString(segment, "text") is { } segText) texts.Add(segText);
        }
        return texts.Count == 0 ? "" : string.Join("\n", texts);
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}
