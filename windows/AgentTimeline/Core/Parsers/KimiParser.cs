using System.Text;
using System.Text.Json;

namespace AgentTimeline.Core.Parsers;

/// <summary>
/// Kimi (Kimi Code CLI) sessions — docs/SESSION-FORMATS.md §3。
///
/// Path:   %USERPROFILE%\.kimi-code\sessions\wd_&lt;project&gt;_&lt;12hex&gt;\
///         session_&lt;uuid&gt;\agents\main\wire.jsonl
///
/// ⚠ 2026-07-28 换代：目录从 `~\.kimi\sessions` 迁到 `~\.kimi-code\sessions`，且 wire
/// 协议消息类型全变（旧的 TurnBegin / TurnEnd / ContentPart 已不存在，全部走顶层
/// `type`）。旧布局不再支持。本机 44 个真实 session 实证。
///
/// 新格式的意外收获：项目目录名自带可读项目名（旧版只有不可解的 hash，只能显示前 8 位）。
///
/// 每行一个 JSON 对象，顶层 `type`：
///   - 用户命令: `type=="turn.prompt"` 且 `origin.kind=="user"` → 拼接 `input[]` 中
///     `type=="text"` 的 `text`；时间戳取顶层 `time`（毫秒 epoch）。
///     不用 `context.append_message` role=user：那条通道混着注入上下文
///     （实测 85 条注入 vs 39 条真实 prompt）。
///   - 回复正文: `type=="context.append_loop_event"` 且 `event.type=="content.part"`
///     且 `event.part.type=="text"` → `event.part.text`。**排除 `part.type=="think"`**
///     （模型思考过程，实测 324 条 think vs 49 条 text，不是它给出的答复）。
///   - 其余 (`metadata` / `config.update` / `tools.*` / `permission.*` /
///     `context.append_message` / `usage.record` / loop 事件 step.*、tool.*) 全部忽略。
/// </summary>
public sealed class KimiParser : IAgentSessionParser
{
    public AgentKind Agent => AgentKind.Kimi;

    /// <summary>session/project 上下文缓存（key = wire.jsonl 路径）。</summary>
    private readonly Dictionary<string, (string SessionId, string Project)> _contexts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 只认主 agent 的 wire：`…/session_&lt;uuid&gt;/agents/main/wire.jsonl`。
    ///
    /// 子 agent（`agents/agent-N/wire.jsonl`）**整文件排除**——与 Claude 侧
    /// `isSidechain` 的语义一致（子 agent 的内部过程不是用户的时间线）。
    /// 实机审计发现：子 agent 目录与 main 共用 `session_&lt;uuid&gt;` 目录名 → 共用
    /// sessionId，而它的"问"是 `origin.kind=system_trigger`（被正确过滤）、"答"却
    /// 是普通 content.part —— 于是 `SetResultLine` 把子 agent 的回复挂到了 main
    /// 的命令节点上。本机 67 个子 agent 文件、63 条回复，实测 5 个节点的结果行被
    /// 抢占且内容完全不相干（"时间不对，重新校准下时间" → "已完成 p2 交叉审核。"），
    /// 并向代号词典写入 4 条只源自子 agent 文本的条目。
    ///
    /// 顺带锚定路径形状（旧实现是裸子串匹配，形状不符时 sessionId 会退化成
    /// ".kimi-code"、project 退化成用户名）。
    /// </summary>
    public bool CanHandle(string path)
    {
        // 分隔符归一：Windows 上是 `\`，冒烟测试跑在 macOS 上会拼出 `/`。
        var p = path.Replace('\\', '/');
        return MainWirePathRegex.IsMatch(p);
    }

    private static readonly System.Text.RegularExpressions.Regex MainWirePathRegex = new(
        @"\.kimi-code/sessions/[^/]+/[^/]+/agents/main/wire\.jsonl$",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public IReadOnlyList<SessionEvent> ParseLines(string path, IReadOnlyList<RawLine> lines)
    {
        var events = new List<SessionEvent>();
        var (sessionId, project) = ContextFor(path);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line.Text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                switch (GetString(root, "type"))
                {
                    case "turn.prompt":
                    {
                        // 只认用户发起的 prompt（system_trigger 等是自动续跑，不是人下的命令）。
                        if (!root.TryGetProperty("origin", out var origin) ||
                            origin.ValueKind != JsonValueKind.Object ||
                            GetString(origin, "kind") != "user")
                        {
                            continue;
                        }

                        var text = ExtractPromptText(root);
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        text = text.Trim();

                        // Short slash commands (e.g. "/model") are UI actions, not prompts.
                        if (text.StartsWith('/') && text.Length <= 24 && !text.Contains(' ')) continue;

                        events.Add(new UserCommand(
                            Agent: AgentKind.Kimi,
                            Project: project,
                            SessionId: sessionId,
                            Timestamp: ParseEpochMillis(root),
                            Text: text,
                            SourceFile: path,
                            SourceOffset: line.ByteOffset));
                        break;
                    }

                    case "context.append_loop_event":
                    {
                        if (!root.TryGetProperty("event", out var ev) ||
                            ev.ValueKind != JsonValueKind.Object ||
                            GetString(ev, "type") != "content.part" ||
                            !ev.TryGetProperty("part", out var part) ||
                            part.ValueKind != JsonValueKind.Object ||
                            GetString(part, "type") != "text")   // "think" = 思考过程，不是答复
                        {
                            continue;
                        }

                        var partText = GetString(part, "text");
                        if (string.IsNullOrWhiteSpace(partText)) continue;

                        // 每块都发；Store 的 SetResultLine 覆盖 session 最新节点，下一次
                        // turn.prompt 之前的最后一块胜出（SESSION-FORMATS.md §3）。
                        events.Add(new TaskComplete(
                            Agent: AgentKind.Kimi,
                            SessionId: sessionId,
                            Timestamp: ParseEpochMillis(root),
                            ResultLine: ParserUtil.ResultExcerpt(partText),
                            FullText: partText)); // untruncated — mined for codenames
                        break;
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

    /// <summary>input[]: concat "text" of items with type=="text".</summary>
    private static string ExtractPromptText(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input) ||
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

    /// <summary>wire 新协议的 `time` 是毫秒 epoch（整数）。</summary>
    private static DateTimeOffset ParseEpochMillis(JsonElement root)
    {
        if (root.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number)
        {
            if (t.TryGetInt64(out var ms) && ms > 0) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            if (t.TryGetDouble(out var d) && d > 0) return DateTimeOffset.FromUnixTimeMilliseconds((long)d);
        }
        return DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// …\wd_&lt;name&gt;_&lt;hash&gt;\session_&lt;uuid&gt;\agents\&lt;agent&gt;\wire.jsonl
    /// → sessionId = session_&lt;uuid&gt; 目录名，project = 工作目录名去壳。
    /// 子 agent（agents\agent-0）与 main 共享同一 sessionId — 与 mac 端一致。
    /// </summary>
    private (string SessionId, string Project) ContextFor(string wirePath)
    {
        if (_contexts.TryGetValue(wirePath, out var cached)) return cached;

        var agentDir = Path.GetDirectoryName(wirePath);         // main
        var agentsDir = Path.GetDirectoryName(agentDir);        // agents
        var sessionDir = Path.GetDirectoryName(agentsDir);      // session_<uuid>
        var projectDir = Path.GetDirectoryName(sessionDir);     // wd_<name>_<12hex>

        var sessionId = Path.GetFileName(sessionDir);
        if (string.IsNullOrEmpty(sessionId)) sessionId = "kimi-session";
        var project = ProjectNameFromWorkDir(Path.GetFileName(projectDir) ?? "");

        var context = (sessionId, project);
        _contexts[wirePath] = context;
        return context;
    }

    /// <summary>
    /// `wd_&lt;name&gt;_&lt;12hex&gt;` → `&lt;name&gt;`。项目名本身可能含下划线
    /// （`wd_hawk_agent-rs_dd8b1189a258` → `hawk_agent-rs`），所以只剥固定前缀与末段
    /// hash；剥不掉就原样用目录名。
    /// </summary>
    public static string ProjectNameFromWorkDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return "kimi";

        var name = dir;
        if (name.StartsWith("wd_", StringComparison.Ordinal)) name = name[3..];

        var sep = name.LastIndexOf('_');
        if (sep >= 0)
        {
            var tail = name[(sep + 1)..];
            if (tail.Length >= 8 && tail.All(IsHexDigit)) name = name[..sep];
        }
        return name.Length == 0 ? dir : name;

        static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
