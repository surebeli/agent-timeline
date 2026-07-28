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
///
/// `session_meta.payload.cwd` 等于摘要器 scratch 目录时**整文件禁用**（见 FileContext.Disabled）：
/// 那份 rollout 是我们自己 `codex exec` 摘要跑出来的，收进来就是自摄取回路。
/// </summary>
public sealed class CodexParser : IAgentSessionParser
{
    public AgentKind Agent => AgentKind.Codex;

    /// <summary>[$plugin:skill](…SKILL.md) 技能调用回显 → 只留 $plugin:skill 徽标文字。</summary>
    private static readonly Regex SkillEchoRegex = new(
        @"^\[(\$[^\]\n]+)\]\([^)\n]*SKILL\.md\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>&lt;task&gt;…&lt;/task&gt; 编排器包装 → 取内文（芯是用户真实任务）。</summary>
    private static readonly Regex TaskWrapperRegex = new(
        @"^<task>\s*(.*?)\s*</task>\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>
    /// codex 侧非人类输入块（裸标签名，容忍带属性）。语料实证：`&lt;heartbeat&gt;` 是
    /// 自动化协调循环自发的轮次（含 automation_id/current_time_iso/instructions），
    /// 不是用户命令；`&lt;user_instructions&gt;`/`&lt;environment_context&gt;` 是环境注入。
    /// </summary>
    private static readonly string[] IgnoredBlocks =
    {
        "<user_instructions", "<environment_context", "<heartbeat",
        "<environments_instructions", "<apps_instructions", "<skills_instructions",
        "<plugins_instructions", "<collaboration_mode", "<multi_agent_mode",
        "<context_window", "<turn_aborted",
    };

    private static bool IsIgnoredCodexBlock(string text)
    {
        foreach (var tag in IgnoredBlocks)
        {
            if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private sealed class FileContext
    {
        public string? SessionId;
        public string? Cwd;
        public bool MetaChecked;

        /// <summary>已应用过本文件的**第一条** session_meta（§4.2b B1）。</summary>
        public bool MetaApplied;

        /// <summary>本文件里最后一个**成功解析**的时间戳（W-e 回退基准）。</summary>
        public DateTimeOffset? LastTimestamp;

        /// <summary>
        /// 整文件忽略：这份 rollout 是**我们自己的摘要器**跑 `codex exec` 产生的。
        ///
        /// 摘要引擎解析到 codex 时，CliSummarizer 以 cwd=%LOCALAPPDATA%\AgentTimeline\summarizer
        /// 起进程，codex 把每条摘要 prompt 原样写成 `user_message` 落进
        /// `~\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl`——路径里不含 "AgentTimeline"/"summarizer"，
        /// SessionWatcher 的路径级排除完全够不着。不认这个 cwd，时间线就会把自己发出的
        /// 每条摘要 prompt 当成用户命令收进来（自摄取回路）。mac 侧同判定。
        /// </summary>
        public bool Disabled;
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
        if (ctx.Disabled) return events;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line.Text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var type = GetString(root, "type");
                // 时间戳（W-e，双端共同规则）：形态宽松解析；解不出就沿用本文件最后一个
                // 成功解析的时间戳；本文件还没有过任何时间戳则这一行不产出事件。
                // 绝不回退 UtcNow——那会让节点跳到时间线顶部，且 ts 参与
                // UNIQUE(agent,session_id,ts,command_hash)，重扫必产生重复行。
                var parsed = ParserUtil.TryParseIsoTimestamp(GetString(root, "timestamp"));
                if (parsed is not null) ctx.LastTimestamp = parsed;
                var timestamp = ctx.LastTimestamp;
                if (!root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                switch (type)
                {
                    case "session_meta":
                        ApplyMeta(ctx, payload);
                        if (ctx.Disabled) return events; // 摘要器自己的 rollout：整文件零事件
                        break;

                    case "event_msg":
                        if (ctx.Disabled || timestamp is not { } ts) break;
                        var payloadType = GetString(payload, "type");
                        if (payloadType == "user_message")
                        {
                            var message = GetString(payload, "message");
                            if (string.IsNullOrWhiteSpace(message)) break;
                            var text = message.Trim();
                            // 环境注入 / 自动化循环发起的轮次，都不是人打的字。
                            // ⚠ 标签一律**裸标签名匹配**（不含 '>'）——harness 会给注入块
                            // 带属性，带 '>' 的前缀匹配不上（与 Claude 侧同一教训）。
                            if (IsIgnoredCodexBlock(text)) break;
                            // 编排器把用户真实任务包在 <task>…</task> 里下发（本机语料 72 条）：
                            // 壳不是人写的，芯是——去壳留正文，否则时间线上 37 个节点的标题
                            // 字面就是 "<task>"（实机审计发现）。
                            if (TaskWrapperRegex.Match(text) is { Success: true } wrapped)
                            {
                                text = wrapped.Groups[1].Value.Trim();
                                if (text.Length == 0) break;
                            }
                            // 插件技能调用回显 [$plugin:skill](本地 SKILL.md 绝对路径) 开头
                            // (语料 17/70 条):保留命令徽标文字,剥掉本机路径(跨机无效
                            // 且泄漏用户名)。见 docs/TEXT-NORMALIZATION.md。
                            text = SkillEchoRegex.Replace(text, "$1", 1).Trim();

                            events.Add(new UserCommand(
                                Agent: AgentKind.Codex,
                                Project: ParserUtil.ProjectNameFromCwd(ctx.Cwd, fallback: "codex"),
                                SessionId: SessionIdFor(path, ctx),
                                Timestamp: ts,
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
                                    Timestamp: ts,
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

    /// <summary>
    /// session_meta 落到上下文——首行直读（EnsureMeta）与增量流式两条路径必须**同一份逻辑**，
    /// 否则重启续扫时摘要器排除会漏掉（自摄取正是在重启后最容易复现）。
    /// </summary>
    private static void ApplyMeta(FileContext ctx, JsonElement payload)
    {
        // §4.2b B1：只应用**本文件第一条** session_meta。被 resume/fork 的 rollout
        // 在文件中途还会写入**原会话**的 meta，逐条重设会让「实时扫」与「重启续扫」
        // （后者只读第 0 行）判出不同 sessionId —— sessionId 参与唯一键，重扫因此
        // 插出重复行；且结果行会被挂到另一个 rollout 文件的命令上。
        ctx.MetaChecked = true;
        if (ctx.MetaApplied) return;
        ctx.MetaApplied = true;
        ctx.SessionId = GetString(payload, "id");
        ctx.Cwd = GetString(payload, "cwd");
        if (AppPaths.IsSummarizerWorkDir(ctx.Cwd)) ctx.Disabled = true;
    }

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
                root.TryGetProperty("payload", out var payload) &&
                payload.ValueKind == JsonValueKind.Object)
            {
                ApplyMeta(ctx, payload);
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
