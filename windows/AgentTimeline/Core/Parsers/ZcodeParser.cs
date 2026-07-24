namespace AgentTimeline.Core.Parsers;

/// <summary>
/// zcode — RESERVED adapter (docs/SESSION-FORMATS.md §4).
///
/// zcode was not installed on the reference machine (2026-07-25) and no session sample
/// exists yet. Per the PRD, the adapter is a placeholder wired into settings:
///   - the user configures the session root directory in 设置 (AppSettings.ZcodeSessionRoot);
///   - the format is assumed to be jsonl;
///   - once a sample is available, implement ParseLines following the same pattern as
///     ClaudeParser / CodexParser / KimiParser — the IAgentSessionParser contract stays as-is.
///
/// This is intentionally the only stubbed body in the scaffold.
/// </summary>
public sealed class ZcodeParser : IAgentSessionParser
{
    private readonly AppSettings _settings;

    public ZcodeParser(AppSettings settings) => _settings = settings;

    public AgentKind Agent => AgentKind.Zcode;

    public bool CanHandle(string path)
    {
        var root = _settings.ZcodeSessionRoot;
        if (string.IsNullOrWhiteSpace(root)) return false;
        return path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
               path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SessionEvent> ParseLines(string path, IReadOnlyList<RawLine> lines)
    {
        // STUB: awaiting a real zcode session sample to define the extraction rules.
        return Array.Empty<SessionEvent>();
    }
}
