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
    /// 按 **grapheme 簇**（UAX-29 文本元素）截断，与 mac `String.count`/`prefix` 同口径
    /// （W6）：只防代理对不够——ZWJ 序列（👨‍👩‍👧 家庭）、变体选择符、肤色修饰、
    /// 组合字（é = e + U+0301）都是多 code unit 的单个"用户感知字符"，从中间切开会
    /// 渲染出半个表情簇或游离的组合符号。超长时截断并补省略号。
    ///
    /// 上限本身用 code unit 作快速路径判定（短于 max 必然不需要截断），只有超长时
    /// 才走簇枚举——正常内容永远不会碰到护栏水位（见 DisplayLimits）。
    /// </summary>
    public static string Clip(string s, int max)
    {
        if (s.Length <= max) return s;

        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        var elements = 0;
        var cut = 0;
        while (enumerator.MoveNext())
        {
            var next = enumerator.ElementIndex + ((string)enumerator.Current).Length;
            if (next > max) break;      // 这一簇会越过上限 → 停在簇边界上
            cut = next;
            elements++;
            if (elements >= max) break; // 簇数也不超过 max（CJK/ASCII 与旧行为一致）
        }
        if (cut == 0) cut = Math.Min(max, s.Length); // 首簇就超长：退化为硬切
        return s[..cut] + "…";
    }

    /// <summary>
    /// 宽松 ISO-8601 → DateTimeOffset，**解不出就返回 null**（双端共同约定，
    /// docs/TEXT-NORMALIZATION.md §4.2 第 14 条）。
    ///
    /// 形态上放宽（`DateTimeOffset.TryParse` 本就吃各种 ISO 变体），但**不再回退
    /// `UtcNow`**：now 回退有两个真实危害——① 节点会跳到时间线顶部，装成"刚发生"；
    /// ② ts 参与 `UNIQUE(agent, session_id, ts, command_hash)`，文件重建/重扫时
    /// 同一条命令每次都拿到新 ts，唯一键失效 → 重复行。
    ///
    /// 调用方（Claude / Codex）拿到 null 时按「沿用本文件最后一个成功解析的时间戳」
    /// 处理；文件里还没有过任何可解析时间戳则丢弃该行。
    /// </summary>
    public static DateTimeOffset? TryParseIsoTimestamp(string? iso)
    {
        if (iso is not null &&
            DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var ts))
        {
            return ts;
        }
        return null;
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
    /// 结果摘录：先过 TextNormalizer（Excerpt 档，docs/TEXT-NORMALIZATION.md §3），
    /// 再取首个非空段落（空行分隔），上限 maxLength（代理对安全截断）。
    /// 折叠态 UI 仍按单行钳制显示；展开态用户可读到完整首段。
    ///
    /// 永不返回空串（§3.4-1）：规整后为空（整段是围栏/表格）时回退到未规整文本，
    /// 否则 Store.SetResultLine 会把已显示的结果行抹掉——审查确认的唯一 UI 回归。
    /// </summary>
    public static string ResultExcerpt(string text, int maxLength = 500)
    {
        var normalized = Text.TextNormalizer.Normalize(text, Text.NormalizeProfile.Excerpt);
        var excerpt = FirstParagraph(normalized);
        if (excerpt.Length == 0) excerpt = FirstParagraph(text.ReplaceLineEndings("\n"));
        return Clip(excerpt, maxLength);
    }

    private static string FirstParagraph(string text)
    {
        var t = text.Trim();
        var end = t.IndexOf("\n\n", StringComparison.Ordinal);
        return (end >= 0 ? t[..end] : t).Trim();
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
