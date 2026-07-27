using System.Text.RegularExpressions;

namespace AgentTimeline.Core;

/// <summary>
/// Codename detection — direct port of macos CodenameDetector (PRD §3.3).
/// Two safe channels into the dictionary:
///   1) dash-style long codenames ("T-PLUGIN-00") match anywhere;
///   2) short batch codes ("N1"/"T2") are too ambiguous for bare matching — they enter
///      only via a definition pattern ("N1: 登录改版"), and afterwards known-code exact
///      matches count as mentions/status updates.
/// </summary>
public static class CodenameDetector
{
    private static readonly Regex DashRegex = new(
        @"\b[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,12}){1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// "N1: xxx" / "**N1**: xxx" / "编号如下：N1: 登录, N2: 支付" — a codename being
    /// (re)defined. Lead-in accepts colons/commas/whitespace so inline and replay-flattened
    /// lists work; the body stops before the next inline "CODE:" (negative lookahead) and at
    /// clause separators, so chained lists yield every code. Exact port of the mac pattern;
    /// RegexOptions.Multiline == NSRegularExpression .anchorsMatchLines.
    /// </summary>
    private static readonly Regex DefinitionRegex = new(
        @"(?:^|[，。；;、,\n：:\s])[\s\-*•·>）)\d.]{0,8}\*{0,2}([A-Z]{1,4}\d{1,3}|[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,12}){1,3})\*{0,2}\s*[:：]\s*((?:(?!\s[\s\-*•·>）)\d.]{0,8}\*{0,2}(?:[A-Z]{1,4}\d{1,3}|[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,12}){1,3})\*{0,2}\s*[:：])[^\n，。；;、,]){2,80})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// Tokens that look like codenames but never are — stored dash/dot-stripped uppercase
    /// so "HTTP-2"/"HTTP2" both hit, and stocked with tech/planning vocabulary that the
    /// short-code definition pattern would otherwise admit (S3/EC2/Q1…).
    /// </summary>
    private static readonly HashSet<string> StopList = new(StringComparer.Ordinal)
    {
        "UTF8", "UTF16", "UTF32", "ISO8601", "SHA256", "SHA1", "MD5",
        "HTTP2", "HTTP3", "TLS1", "OAUTH2", "OAUTH20", "BASE64",
        "GPT4", "GPT5", "JSONRPC", "GRPCWEB", "XY", "AB", "QA",
        "S3", "EC2", "R2", "B2", "K8", "X86", "X64", "I18N", "L10N",
        "V1", "V2", "V3", "V4", "V5", "Q1", "Q2", "Q3", "Q4", "H1", "H2",
        "P0", "P1", "P2", "MP3", "MP4",
    };

    /// <summary>
    /// Sanity gate for LLM-extracted names — the model occasionally emits list indices
    /// ("1") or punctuation as "codenames".
    /// </summary>
    public static bool IsPlausibleName(string name) =>
        name.Length >= 2 && name.Length <= 24
            && name.Any(char.IsLetter)
            && !IsStopped(name);

    public static bool IsStopped(string name)
    {
        var normalized = name
            .Replace("-", "")
            .Replace(".", "")
            .ToUpperInvariant();
        return StopList.Contains(normalized);
    }

    /// <summary>Dash-style codenames mentioned anywhere in the text (order of appearance, deduped).</summary>
    public static IReadOnlyList<string> Detect(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (Match m in DashRegex.Matches(text))
        {
            var name = m.Value;
            if (IsStopped(name) || seen.Contains(name)) continue;
            // Too-short tokens like "M-1"/"A-B" are noise; real codenames carry
            // either a digit ("T-PLUGIN-00") or enough length ("FEAT-LOGIN").
            if (name.Length < 4) continue;
            if (!name.Any(char.IsDigit) && name.Length < 5) continue;
            seen.Add(name);
            result.Add(name);
        }
        return result;
    }

    /// <summary>Codenames being defined in this text ("N1: xxx"), short codes included.</summary>
    public static IReadOnlyList<(string Name, string Definition)> DetectDefinitions(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, string)>();
        foreach (Match m in DefinitionRegex.Matches(text))
        {
            var name = m.Groups[1].Value;
            var definition = m.Groups[2].Value.Trim();
            if (IsStopped(name) || !seen.Add(name) || definition.Length == 0) continue;
            result.Add((name, definition));
        }
        return result;
    }

    /// <summary>
    /// Exact word-boundary occurrences of already-known codenames, with a status inferred
    /// from the surrounding clause window.
    /// </summary>
    public static IReadOnlyList<(string Name, CodenameStatus? Status, string Context)> DetectMentions(
        string text, IReadOnlyCollection<string> known)
    {
        var result = new List<(string, CodenameStatus?, string)>();
        if (known.Count == 0) return result;
        foreach (var name in known)
        {
            var searchStart = 0;
            (CodenameStatus? Status, string Context)? found = null;
            while (searchStart <= text.Length - name.Length)
            {
                var hit = text.IndexOf(name, searchStart, StringComparison.Ordinal);
                if (hit < 0) break;
                searchStart = hit + name.Length;
                // Word boundary against ASCII alnum only — "T1" inside "T12"/"AT1" is a
                // different token, but CJK abutting ("N2完成") is natural.
                if (hit > 0 && IsAsciiAlnum(text[hit - 1])) continue;
                var hitEnd = hit + name.Length;
                if (hitEnd < text.Length && IsAsciiAlnum(text[hitEnd])) continue;

                var window = Clause(text, hit, hitEnd);
                var status = InferStatus(window);
                // Prefer the occurrence that carries a status signal.
                if (found is null || (found.Value.Status is null && status is not null))
                {
                    found = (status, window.Replace("\n", " "));
                }
                if (status is not null) break;
            }
            if (found is { } f) result.Add((name, f.Status, f.Context));
        }
        return result;
    }

    private static bool IsAsciiAlnum(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    private static readonly HashSet<char> ClauseSeparators = new("，。；;,、\n！？");

    /// <summary>
    /// The clause containing the hit — status keywords from neighbouring clauses
    /// ("N3变更，N1 继续") must not bleed into this codename's window.
    /// (Steps count UTF-16 units here vs grapheme clusters on mac; identical for CJK/ASCII.)
    /// </summary>
    private static string Clause(string text, int hitStart, int hitEnd)
    {
        var start = hitStart;
        var steps = 0;
        while (start > 0 && steps < 20)
        {
            if (ClauseSeparators.Contains(text[start - 1])) break;
            start--;
            steps++;
        }
        var end = hitEnd;
        steps = 0;
        while (end < text.Length && steps < 24)
        {
            if (ClauseSeparators.Contains(text[end])) break;
            end++;
            steps++;
        }
        // 摘录进词典面板/chip popover 展示 → 过 Mining 档规整（仅行内 unwrap，
        // 不做块级 skip：窗口仅 ~44 字符，skip 会掏空）。状态推断吃的是本函数
        // 返回值，unwrap 只去标记不改语义关键词，不影响 InferStatus。
        return Text.TextNormalizer.Normalize(text[start..end], Text.NormalizeProfile.Mining);
    }

    private static readonly string[] ChangedKeywords =
        { "变更", "调整", "改动", "修改", "重新设计", "rework" };
    private static readonly string[] CompletedKeywords =
        { "完成", "收口", "验收", "已实现", "done", "closed", "finished", "搞定", "修复了" };
    private static readonly string[] ActiveKeywords =
        { "开始", "执行", "推进", "继续", "进行中", "启动", "开展", "接下去", "接下来", "in progress", "working" };

    private static readonly HashSet<char> NegationChars = new("未没不别无非");

    public static CodenameStatus? InferStatus(string window)
    {
        if (ContainsKeyword(window, ChangedKeywords)) return CodenameStatus.Changed;
        if (ContainsKeyword(window, CompletedKeywords)) return CodenameStatus.Completed;
        if (ContainsKeyword(window, ActiveKeywords)) return CodenameStatus.Active;
        return null;
    }

    /// <summary>
    /// Keyword hit that is NOT negated — "尚未完成"/"不执行" must not record 完成/进行中.
    /// Negation = one of 未没不别无非 within the two characters immediately before the keyword.
    /// </summary>
    private static bool ContainsKeyword(string window, string[] keywords)
    {
        var lower = window.ToLowerInvariant();
        foreach (var keyword in keywords)
        {
            var searchStart = 0;
            while (searchStart <= lower.Length - keyword.Length)
            {
                var hit = lower.IndexOf(keyword, searchStart, StringComparison.Ordinal);
                if (hit < 0) break;
                searchStart = hit + keyword.Length;
                var negated = false;
                for (var back = 1; back <= 2 && hit - back >= 0; back++)
                {
                    if (NegationChars.Contains(lower[hit - back]))
                    {
                        negated = true;
                        break;
                    }
                }
                if (!negated) return true;
            }
        }
        return false;
    }
}
