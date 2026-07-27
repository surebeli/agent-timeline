using System.Text.Json;

namespace AgentTimeline.Core.Parsers;

/// <summary>
/// zcode (Z Code CLI) agent 任务会话 — 实机样例逆向（2026-07-27，ZCode 3.5.2）。
///
/// 路径:   %USERPROFILE%\.zcode\cli\agents\sess_&lt;uuid&gt;\agent_&lt;uuid&gt;\transcript.jsonl
///         （settings.ZcodeSessionRoot 可自定义根目录；同目录 metadata.json 为 sidecar：
///          cwd → 项目名、description/status 等）
/// 格式:   每行 {id, sessionId, turnId, type, timestamp(ISO8601), sequenceNumber, payload}
///         type ∈ turn_started / turn_complete / model_request / model_streaming /
///                tool_call_scheduled / … （中间过程事件全部忽略）
///
///   - 任务下发:  type=="turn_started" → payload.input（该 agent 领到的任务原文）；
///   - 任务完成:  type=="turn_complete" → payload.response（最终回答全文，首行作 resultLine，
///                全文参与代号挖掘）。
///
/// 注：agents 目录内是 zcode 的任务派发记录（每个 agent_* 目录一次任务），主会话的
/// 人机对话不落此目录 — 时间线粒度即「一次任务 = 一个节点」。
/// </summary>
public sealed class ZcodeParser : IAgentSessionParser
{
    /// <summary>Sidecar metadata.json 解析出的项目名缓存（key = transcript 路径）。</summary>
    private readonly Dictionary<string, string> _projectNames = new();

    public AgentKind Agent => AgentKind.Zcode;

    // transcript.jsonl 文件名在四家 agent 中唯一（claude=<uuid>.jsonl, codex=rollout-*,
    // kimi=wire.jsonl），无需依赖根路径前缀 — 自定义 ZcodeSessionRoot 也能命中。
    public bool CanHandle(string path) =>
        string.Equals(Path.GetFileName(path), "transcript.jsonl", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<SessionEvent> ParseLines(string path, IReadOnlyList<RawLine> lines)
    {
        var events = new List<SessionEvent>();
        // agent_<uuid> 目录名作 sessionId（每任务一目录，与行内 sessionId 等价且更稳定）。
        var sessionId = Path.GetFileName(Path.GetDirectoryName(path)) ?? "zcode-agent";
        var project = ProjectNameFor(path);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line.Text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var type = GetString(root, "type");
                if (type != "turn_started" && type != "turn_complete") continue;
                if (!root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var timestamp = ParseTimestamp(root);
                if (type == "turn_started")
                {
                    var input = GetString(payload, "input")?.Trim();
                    if (string.IsNullOrWhiteSpace(input)) continue;
                    events.Add(new UserCommand(
                        Agent: AgentKind.Zcode,
                        Project: project,
                        SessionId: sessionId,
                        Timestamp: timestamp,
                        Text: input,
                        SourceFile: path,
                        SourceOffset: line.ByteOffset));
                }
                else // turn_complete
                {
                    var response = GetString(payload, "response");
                    if (string.IsNullOrWhiteSpace(response)) continue;
                    events.Add(new TaskComplete(
                        Agent: AgentKind.Zcode,
                        SessionId: sessionId,
                        Timestamp: timestamp,
                        ResultLine: ParserUtil.ResultExcerpt(response),
                        FullText: response)); // untruncated — mined for codenames
                }
            }
            catch (JsonException)
            {
                // Skip malformed line.
            }
        }
        return events;
    }

    private static DateTimeOffset ParseTimestamp(JsonElement root) =>
        ParserUtil.ParseIsoTimestamp(GetString(root, "timestamp"));

    /// <summary>
    /// 项目显示名：同目录 metadata.json 的 cwd 末段；缺 sidecar 时回退 sess_ 目录名截断。
    /// </summary>
    private string ProjectNameFor(string transcriptPath)
    {
        if (_projectNames.TryGetValue(transcriptPath, out var cached)) return cached;

        var agentDir = Path.GetDirectoryName(transcriptPath);
        var sessDir = agentDir is null ? null : Path.GetDirectoryName(agentDir);
        var fallback = Path.GetFileName(sessDir ?? "") is { Length: > 0 } sess
            ? (sess.Length > 13 ? sess[..13] : sess) // "sess_" + uuid 前 8
            : "zcode";
        var name = fallback;

        try
        {
            var metaPath = agentDir is null ? null : Path.Combine(agentDir, "metadata.json");
            if (metaPath is not null && File.Exists(metaPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                name = ParserUtil.ProjectNameFromCwd(GetString(doc.RootElement, "cwd"), fallback);
            }
        }
        catch
        {
            // metadata.json is advisory only.
        }

        _projectNames[transcriptPath] = name;
        return name;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
