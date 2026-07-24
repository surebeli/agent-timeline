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
/// TaskComplete { agent, sessionId, timestamp, resultLine }
/// </summary>
public sealed record TaskComplete(
    AgentKind Agent,
    string SessionId,
    DateTimeOffset Timestamp,
    string ResultLine) : SessionEvent;

/// <summary>Where a summary came from (affects retry logic and UI hinting).</summary>
public enum SummarySource
{
    Rule,
    Cli,
    Provider,
}

/// <summary>A codename with the definition the LLM extracted for it (may be null for regex-only hits).</summary>
public sealed record CodenameDefinition(string Name, string? Definition);

/// <summary>
/// LLM (or rule-based) digest of one user command. Matches the shared JSON contract
/// in docs/ARCHITECTURE.md: {title, keyPoints[], codenames[{name, definition}], resultLine}.
/// </summary>
public sealed record Summary(
    string Title,
    IReadOnlyList<string> KeyPoints,
    IReadOnlyList<CodenameDefinition> Codenames,
    string? ResultLine,
    SummarySource Source);

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
}
