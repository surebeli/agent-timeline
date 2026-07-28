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
    /// <summary>
    /// 注入 / 元信息块前缀——绝不能作为用户命令冒出来。与 mac
    /// `ParserSupport.ignoredPrefixes` **逐条一致**，两端共用同一份语义；
    /// 各 agent 解析器共用它，避免各自维护一份而慢慢漂移。
    ///
    /// ⚠ 一律**裸标签名**（不含 '&gt;'）：harness 会给注入块带属性，带 '&gt;' 的
    /// 前缀匹配不上（Claude/Codex 两侧都栽过这个跟头）。
    ///
    /// NOTE: 斜杠命令回显块（`&lt;command-name&gt;` / `&lt;command-message&gt;`）**故意不在此列**
    /// ——它们承载真实的用户命令，是转换而非丢弃（见 ClaudeParser.ParseAttachmentLine）。
    /// </summary>
    public static readonly string[] IgnoredPrefixes =
    {
        "<local-command-caveat", "<local-command-stdout",
        "<system-reminder", "<user_instructions", "<environment_context", "<task-notification",
        // `!cmd` 直通 shell 的**输出**（实机 W0 验证时发现的泄漏，本机语料 10 条）：
        // 输入侧是用户真实操作、由 ClaudeParser.BashInputRegex 转换保留，输出侧不是人说的话。
        "<bash-stdout", "<bash-stderr",
        "Caveat:", "[Request interrupted",
        "This session is being continued from",  // post-compaction continuation blob
    };

    /// <summary>与 mac `ParserSupport.isIgnoredContent` 同口径（trim 后按前缀判定）。</summary>
    public static bool IsIgnoredContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        foreach (var prefix in IgnoredPrefixes)
        {
            if (t.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

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
        var body = DropLeadingHeadings(text.ReplaceLineEndings("\n"));
        var normalized = Text.TextNormalizer.Normalize(body, Text.NormalizeProfile.Excerpt);
        var excerpt = FirstParagraph(normalized);
        // 兜底链：剥标题后为空 → 用原文（含标题）再规整；仍为空 → 未规整原文。
        // 永不返回空串（§3.4-1）。
        if (excerpt.Length == 0)
        {
            excerpt = FirstParagraph(
                Text.TextNormalizer.Normalize(text, Text.NormalizeProfile.Excerpt));
        }
        if (excerpt.Length == 0) excerpt = FirstParagraph(text.ReplaceLineEndings("\n"));
        return Clip(excerpt, maxLength);
    }

    /// <summary>
    /// 剥掉回复开头的 markdown 标题行，让首段落在**正文**上。
    ///
    /// 实机审计：Kimi 的回复几乎总以 `## Summary` / `# RCA Output — …` 开头，规整后
    /// 首段就是那一个词——用户库里 kimi 结果行有 7 条字面就是 "Summary"，
    /// ≤12 字符的占比 38.9%（对比 codex 4.0% / claude 3.8% / zcode 0%）。
    /// 判据严格用「行首 #{1,6} + 空格」（与 §3.3 标题规则同一判据，不误伤 `#include`），
    /// 只在开头连续剥；正文里的标题不动。全篇皆标题时由上面的兜底链回到原文。
    /// </summary>
    private static string DropLeadingHeadings(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var lineEnd = text.IndexOf('\n', i);
            var line = (lineEnd < 0 ? text[i..] : text[i..lineEnd]).Trim();
            if (line.Length == 0)                       // 跳过空行继续看下一行
            {
                if (lineEnd < 0) break;
                i = lineEnd + 1;
                continue;
            }
            if (!IsAtxHeading(line)) break;             // 遇到正文行 → 停
            if (lineEnd < 0) return "";                 // 全篇皆标题
            i = lineEnd + 1;
        }
        return i == 0 ? text : text[i..];

        static bool IsAtxHeading(string line)
        {
            var hashes = 0;
            while (hashes < line.Length && line[hashes] == '#') hashes++;
            return hashes is >= 1 and <= 6 &&
                   hashes < line.Length &&
                   (line[hashes] == ' ' || line[hashes] == '\t');
        }
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
