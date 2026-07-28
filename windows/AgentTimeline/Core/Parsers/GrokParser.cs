using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentTimeline.Core.Parsers;

/// <summary>
/// Grok Build sessions — docs/SESSION-FORMATS.md §3.
///
/// Path:   %USERPROFILE%\.grok\sessions\&lt;URL 编码的 cwd&gt;\&lt;session-uuid&gt;\updates.jsonl
/// Format: 每行一条 ACP（Agent Client Protocol）通知
///         {timestamp, method:"session/update", params:{sessionId, update:{sessionUpdate, …}}}
///
///   - user_message_chunk  → params.update.content.text（用户命令）
///   - agent_message_chunk → 暂存；一个轮次内有多条（工具调用之间的进度旁白）
///   - turn_completed      → 把**最后一条**暂存的 agent 消息作结果行
///
/// 项目名只能由目录名解码得到——文件里**没有任何 cwd 字段**（本机 87 个 session 实证）。
/// </summary>
public sealed class GrokParser : IAgentSessionParser
{
    public AgentKind Agent => AgentKind.Grok;

    /// <summary>
    /// 必须锚定到 `updates.jsonl`：同一棵会话树下并存 6 种 `.jsonl`
    /// （chat_history 91 / events 91 / updates 87 / rewind_points 81 /
    /// hunk_records 4 / prompt_history 3），宽松匹配会把同一轮对话重复摄取
    /// （Kimi 侧 A1 同类教训）。
    /// </summary>
    private static readonly Regex UpdatesPathRegex = new(
        @"\.grok/sessions/[^/]+/[^/]+/updates\.jsonl$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed class FileContext
    {
        public string? SessionId;
        public string? Project;

        /// <summary>本文件里最后一个**成功解析**的时间戳（与 Claude/Codex 同口径的回退基准）。</summary>
        public DateTimeOffset? LastTimestamp;

        /// <summary>
        /// 当前轮次里最后一条 agent 消息，等 `turn_completed` 落为结果行。
        /// 重启续扫时若 offset 已越过这些行，则本轮无暂存 → 该轮不产出结果行
        /// （不猜、不回退到旁白，宁可少一条也不挂错）。
        /// </summary>
        public string? PendingAgentText;
        public DateTimeOffset? PendingAgentTs;

        /// <summary>解码出的 cwd 命中摘要器 scratch 目录 → 整文件忽略（自摄取回路防护）。</summary>
        public bool Disabled;
    }

    private readonly Dictionary<string, FileContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    public bool CanHandle(string path) => UpdatesPathRegex.IsMatch(path.Replace('\\', '/'));

    public IReadOnlyList<SessionEvent> ParseLines(string path, IReadOnlyList<RawLine> lines)
    {
        var events = new List<SessionEvent>();
        if (lines.Count == 0) return events;

        if (!_contexts.TryGetValue(path, out var ctx))
        {
            ctx = new FileContext();
            SeedFromPath(path, ctx);
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

                // ⚠ timestamp 是 unix **整秒**（int），不是 ISO8601——TryParseIsoTimestamp
                // 解不了。解不出就沿用本文件最后一个成功值；从没有过则丢弃该行
                // （绝不回退 UtcNow：ts 参与 UNIQUE 键，重扫必产生重复行）。
                var parsed = TryParseUnixSeconds(root);
                if (parsed is not null) ctx.LastTimestamp = parsed;
                var timestamp = ctx.LastTimestamp;

                if (!root.TryGetProperty("params", out var prms) ||
                    prms.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                if (GetString(prms, "sessionId") is { Length: > 0 } sid) ctx.SessionId = sid;
                if (!prms.TryGetProperty("update", out var update) ||
                    update.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                switch (GetString(update, "sessionUpdate"))
                {
                    case "user_message_chunk":
                    {
                        if (timestamp is not { } ts) break;
                        var text = ContentText(update);
                        // L1 用双端共享清单（ParserUtil.IgnoredPrefixes ≡ mac
                        // ParserSupport.ignoredPrefixes）。本机语料实际命中的是
                        // `<system-reminder>` 后台任务回执（92 条用户消息里 4 条）。
                        if (ParserUtil.IsIgnoredContent(text)) break;
                        text = text!.Trim();
                        events.Add(new UserCommand(
                            Agent: AgentKind.Grok,
                            Project: ctx.Project ?? "grok",
                            SessionId: SessionIdFor(path, ctx),
                            Timestamp: ts,
                            Text: text,
                            SourceFile: path,
                            SourceOffset: line.ByteOffset));
                        break;
                    }

                    case "agent_message_chunk":
                    {
                        // 名字里是 chunk，实际一条即一条完整消息（无需拼接）。工具调用
                        // 之间的进度旁白也走这个通道（实测 532 条对 57 个 turn_completed），
                        // 只有轮次结束前的最后一条是给用户的答复 → 一路覆盖暂存。
                        var text = ContentText(update);
                        if (string.IsNullOrWhiteSpace(text)) break;
                        ctx.PendingAgentText = text;
                        ctx.PendingAgentTs = timestamp;
                        break;
                    }

                    case "turn_completed":
                    {
                        var text = ctx.PendingAgentText;
                        var ts = ctx.PendingAgentTs ?? timestamp;
                        ctx.PendingAgentText = null;
                        ctx.PendingAgentTs = null;
                        if (string.IsNullOrWhiteSpace(text) || ts is not { } at) break;
                        events.Add(new TaskComplete(
                            Agent: AgentKind.Grok,
                            SessionId: SessionIdFor(path, ctx),
                            Timestamp: at,
                            ResultLine: ParserUtil.ResultExcerpt(text),
                            FullText: text)); // untruncated — mined for codenames
                        break;
                    }

                    // tool_call / tool_call_update / hook_execution / agent_thought_chunk /
                    // plan / task_backgrounded / task_completed / session_recap → 全部忽略。
                    // ⚠ task_completed 是子任务/工具完成，不是轮次完成，不可当结果行。
                }
            }
            catch (JsonException)
            {
                // Skip malformed line.
            }
        }
        return events;
    }

    /// <summary>
    /// 从路径播种 sessionId 与项目名：`…\sessions\&lt;URL 编码的 cwd&gt;\&lt;uuid&gt;\updates.jsonl`。
    /// 目录名是百分号编码的工作目录绝对路径
    /// （`F%3A%5Cworkspace%5Cproject%5Chawk-watcher` → `F:\workspace\project\hawk-watcher`；
    /// mac 侧 `%2FUsers%2F…` 同理），解码后取末段作项目名。
    /// </summary>
    private static void SeedFromPath(string path, FileContext ctx)
    {
        try
        {
            var sessionDir = Path.GetDirectoryName(path);
            if (sessionDir is null) return;
            ctx.SessionId = Path.GetFileName(sessionDir);

            var projectDir = Path.GetFileName(Path.GetDirectoryName(sessionDir));
            if (string.IsNullOrEmpty(projectDir)) return;
            var cwd = Uri.UnescapeDataString(projectDir);
            ctx.Project = ParserUtil.ProjectNameFromCwd(cwd, fallback: "grok");
            if (AppPaths.IsSummarizerWorkDir(cwd)) ctx.Disabled = true;
        }
        catch (Exception ex)
        {
            // 编码异常的目录名不该让整条通道停摆——退化成 fallback 项目名。
            Log.Warn($"GrokParser: failed to seed context from {path}: {ex.Message}");
        }
    }

    private static string SessionIdFor(string path, FileContext ctx) =>
        ctx.SessionId ?? Path.GetFileName(Path.GetDirectoryName(path)) ?? "grok";

    /// <summary>`update.content.text`（content 是单个对象，不是数组）。</summary>
    private static string? ContentText(JsonElement update) =>
        update.TryGetProperty("content", out var content) &&
        content.ValueKind == JsonValueKind.Object
            ? GetString(content, "text")
            : null;

    private static DateTimeOffset? TryParseUnixSeconds(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var t)) return null;
        // 数值形态是常态（本机 27724/27724 行皆 int）；字符串形态容错处理。
        if (t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var secs))
        {
            return FromUnixSeconds(secs);
        }
        if (t.ValueKind == JsonValueKind.String &&
            long.TryParse(t.GetString(), out var parsed))
        {
            return FromUnixSeconds(parsed);
        }
        return null;
    }

    private static DateTimeOffset? FromUnixSeconds(long secs)
    {
        // DateTimeOffset 的合法 unix 秒区间之外（脏数据/占位 0）一律判为解析失败，
        // 交给调用方走"沿用上一个时间戳"的既有回退路径。
        if (secs <= 0 || secs > 253402300799) return null;
        return DateTimeOffset.FromUnixTimeSeconds(secs);
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
