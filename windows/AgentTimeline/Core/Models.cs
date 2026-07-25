// Core domain models — mirrors macos Sources/AgentTimeline/Core/Models.swift.
// Normalized event shapes are defined in docs/SESSION-FORMATS.md ("归一化事件").

namespace AgentTimeline.Core;

/// <summary>Which agent CLI produced a session file.</summary>
public enum AgentKind
{
    Claude,
    Codex,
    Kimi,
    Zcode,
}

public static class AgentKindExtensions
{
    /// <summary>Stable string used in the SQLite store and design-token lookups.</summary>
    public static string Key(this AgentKind kind) => kind switch
    {
        AgentKind.Claude => "claude",
        AgentKind.Codex => "codex",
        AgentKind.Kimi => "kimi",
        AgentKind.Zcode => "zcode",
        _ => "unknown",
    };

    public static string DisplayName(this AgentKind kind) => kind switch
    {
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        AgentKind.Kimi => "Kimi",
        AgentKind.Zcode => "zcode",
        _ => "?",
    };

    public static AgentKind FromKey(string key) => key switch
    {
        "claude" => AgentKind.Claude,
        "codex" => AgentKind.Codex,
        "kimi" => AgentKind.Kimi,
        "zcode" => AgentKind.Zcode,
        _ => AgentKind.Claude,
    };
}

/// <summary>
/// Codename lifecycle states (PRD §3.3 状态机: 定义 → 进行中 → 完成 / 变更).
/// Mirrors macos CodenameStatus; persisted as the Chinese label (the mac rawValue)
/// so the two stores stay directly comparable.
/// </summary>
public enum CodenameStatus
{
    Defined,    // 定义
    Active,     // 进行中
    Completed,  // 完成
    Changed,    // 变更
    Mentioned,  // 提及
}

public static class CodenameStatuses
{
    public static string Label(this CodenameStatus status) => status switch
    {
        CodenameStatus.Defined => "定义",
        CodenameStatus.Active => "进行中",
        CodenameStatus.Completed => "完成",
        CodenameStatus.Changed => "变更",
        CodenameStatus.Mentioned => "提及",
        _ => "",
    };

    public static CodenameStatus? FromLabel(string? label) => label switch
    {
        "定义" => CodenameStatus.Defined,
        "进行中" => CodenameStatus.Active,
        "完成" => CodenameStatus.Completed,
        "变更" => CodenameStatus.Changed,
        "提及" => CodenameStatus.Mentioned,
        _ => null,
    };
}

/// <summary>
/// Node phase classification (PRD §3.3b 阶段锚点) — the "anchor" facet of the timeline.
/// Mirrors macos NodeKind; persisted/filtered by the Chinese label (the mac rawValue).
/// </summary>
public enum NodeKind
{
    Requirement, // 需求
    Task,        // 任务
    Research,    // 调研
    Learning,    // 学习
    Decision,    // 决策
    Fix,         // 修复
    Other,       // 其他
}

public static class NodeKinds
{
    /// <summary>All labels in declaration order (drives the UI kind filter).</summary>
    public static readonly IReadOnlyList<string> AllLabels =
        new[] { "需求", "任务", "调研", "学习", "决策", "修复", "其他" };

    public static string Label(this NodeKind kind) => AllLabels[(int)kind];

    /// <summary>Returns the label when valid, else null (LLM output is untrusted).</summary>
    public static string? Normalize(string? label) =>
        label is not null && AllLabels.Contains(label) ? label : null;
}

/// <summary>Base type for events normalized out of any agent session file.</summary>
public abstract record SessionEvent;

/// <summary>
/// A command the USER submitted to an agent.
/// UserCommand { agent, project, sessionId, timestamp, text, sourceFile, sourceOffset }
/// </summary>
public sealed record UserCommand(
    AgentKind Agent,
    string Project,
    string SessionId,
    DateTimeOffset Timestamp,
    string Text,
    string SourceFile,
    long SourceOffset) : SessionEvent;

/// <summary>
/// Agent finished a turn/task; ResultLine is a one-line description of the outcome.
/// FullText carries the untruncated assistant text — definitions frequently live in the
/// reply ("好的，编号如下：N1: …"), so the coordinator mines it for codenames (PRD §3.3
/// 来源覆盖: agent 回复).
/// TaskComplete { agent, sessionId, timestamp, resultLine, fullText }
/// </summary>
public sealed record TaskComplete(
    AgentKind Agent,
    string SessionId,
    DateTimeOffset Timestamp,
    string ResultLine,
    string? FullText = null) : SessionEvent;

/// <summary>Where a summary came from (affects retry logic and UI hinting).</summary>
public enum SummarySource
{
    Rule,
    Cli,
    Provider,
}

/// <summary>
/// A codename with the definition the LLM extracted for it (may be null for regex-only hits).
/// Status is a CodenameStatus label (定义/进行中/完成/变更/提及) when the extractor saw a
/// lifecycle signal; null on regex-only hits and on cached pre-lifecycle rows.
/// </summary>
public sealed record CodenameDefinition(string Name, string? Definition, string? Status = null);

/// <summary>
/// LLM (or rule-based) digest of one user command. Matches the shared JSON contract
/// (macos SummaryPrompt): {title, kind, keyPoints[], codenames[{name, definition, status}],
/// resultLine}. Kind is a NodeKind label; null on cached pre-lifecycle rows.
/// </summary>
public sealed record Summary(
    string Title,
    IReadOnlyList<string> KeyPoints,
    IReadOnlyList<CodenameDefinition> Codenames,
    string? ResultLine,
    SummarySource Source,
    string? Kind = null);

/// <summary>One timeline entry = one user command + its (possibly pending) summary.</summary>
public sealed class TimelineNode
{
    public long Id { get; set; }
    public required UserCommand Command { get; init; }
    public required Summary Summary { get; set; }
    /// <summary>SHA-256 of (agent + text); key of the summary cache.</summary>
    public required string CommandHash { get; init; }
    /// <summary>True while an LLM summary has not replaced the rule-based one yet.</summary>
    public bool SummaryPending { get; set; }
}

/// <summary>Dictionary entry for a task codename (代号词典项).</summary>
public sealed class CodenameEntry
{
    public required string Name { get; init; }
    public DateTimeOffset FirstSeen { get; set; }
    /// <summary>Node (user command) where the codename first appeared — its "definition site".</summary>
    public long DefiningNodeId { get; set; }
    public string? Definition { get; set; }
    /// <summary>Short excerpt of the first command around the first occurrence.</summary>
    public string ContextExcerpt { get; set; } = "";
    public int Occurrences { get; set; }

    // --- Lifecycle (PRD §3.3, 2026-07-26) ---

    /// <summary>CodenameStatus label; empty until a lifecycle signal is seen.</summary>
    public string Status { get; set; } = "";
    /// <summary>Node that last advanced the status machine (0 = none yet).</summary>
    public long StatusNodeId { get; set; }
    /// <summary>Most recent definition/mention time; null on pre-lifecycle rows.</summary>
    public DateTimeOffset? Updated { get; set; }
    /// <summary>Clause excerpt around the most recent mention ("…N2完成，开始 N3…").</summary>
    public string LastContext { get; set; } = "";

    public CodenameStatus? StatusValue => CodenameStatuses.FromLabel(Status);
}
