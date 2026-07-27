namespace AgentTimeline.Core.Parsers;

/// <summary>One decoded line of a session file plus the byte offset where it starts.</summary>
public readonly record struct RawLine(long ByteOffset, string Text);

/// <summary>
/// Per-agent session parser — mirrors macos AgentSessionParser.swift:
/// canHandle(url) / parse(newData, context) -&gt; [SessionEvent].
/// Implementations follow docs/SESSION-FORMATS.md EXACTLY (shared spec for both platforms).
/// Parsers may keep per-file context (e.g. Codex session_meta) keyed by path.
/// All parsing is defensive: a malformed line is skipped, never throws out of ParseLines.
/// </summary>
public interface IAgentSessionParser
{
    AgentKind Agent { get; }

    /// <summary>Whether this parser owns the given session file path.</summary>
    bool CanHandle(string path);

    /// <summary>Parses freshly appended lines of <paramref name="path"/> into normalized events.</summary>
    IReadOnlyList<SessionEvent> ParseLines(string path, IReadOnlyList<RawLine> lines);
}

internal static class ParserUtil
{
    /// <summary>
    /// 按 UTF-16 code unit 截断但不劈开代理对（emoji 等增补平面字符）：孤立代理在 UI
    /// 显示替换符、经 System.Text.Json 序列化变 U+FFFD 乱码。超长时截断并补省略号。
    /// </summary>
    public static string Clip(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut] + "…";
    }

    /// <summary>Lenient ISO-8601 → DateTimeOffset; falls back to now (UTC) so events never get dropped over a timestamp.</summary>
    public static DateTimeOffset ParseIsoTimestamp(string? iso)
    {
        if (iso is not null &&
            DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var ts))
        {
            return ts;
        }
        return DateTimeOffset.UtcNow;
    }

    /// <summary>Last path segment of a cwd recorded with either separator ("/Users/x/foo" or "C:\x\foo").</summary>
    public static string ProjectNameFromCwd(string? cwd, string fallback)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return fallback;
        var name = cwd.Replace('\\', '/').TrimEnd('/');
        var idx = name.LastIndexOf('/');
        var leaf = idx >= 0 ? name[(idx + 1)..] : name;
        return string.IsNullOrWhiteSpace(leaf) ? fallback : leaf;
    }

    /// <summary>
    /// 结果摘录：取首个非空段落（空行分隔）而非首行，上限 maxLength（代理对安全截断）。
    /// 折叠态 UI 仍按单行钳制显示；展开态用户可读到完整首段（实机反馈：原先解析期
    /// 就截成 160 字符单行，展开也无内容可看）。mac 端待同步同一语义。
    /// </summary>
    public static string ResultExcerpt(string text, int maxLength = 500)
    {
        var normalized = text.ReplaceLineEndings("\n").Trim();
        var end = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var paragraph = (end >= 0 ? normalized[..end] : normalized).Trim();
        return Clip(paragraph, maxLength);
    }

    public static string FirstLine(string text, int maxLength)
    {
        var line = text.ReplaceLineEndings("\n");
        var nl = line.IndexOf('\n');
        if (nl >= 0) line = line[..nl];
        line = line.Trim();
        return line.Length <= maxLength ? line : line[..maxLength] + "…";
    }
}
