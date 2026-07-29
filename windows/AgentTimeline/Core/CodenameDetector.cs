using System.Text;
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

    private static readonly HashSet<char> ClauseSeparators = new("，。；;,、\n！？·");

    /// <summary>
    /// 该位置是否是分句点。
    ///
    /// ASCII <c>. ! ?</c> 只在**句末形态**（后面是空白或串尾）时才算——韩语句子 100% 用
    /// ASCII 句点收尾，不认它的话子句窗口永远不会在句号处切断、只会撞上长度上限，
    /// 邻句的状态词会大量串味（日/韩术语调研实测发现）。但不能无条件认：
    /// <c>v0.6.0</c> / <c>a.txt</c> / <c>1.5</c> 里的点会把窗口从中间截断。
    /// </summary>
    private static bool IsClauseBreak(string text, int index)
    {
        var c = text[index];
        if (ClauseSeparators.Contains(c)) return true;
        if (c is '.' or '!' or '?')
        {
            return index + 1 >= text.Length || char.IsWhiteSpace(text[index + 1]);
        }
        return false;
    }

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
            if (IsClauseBreak(text, start - 1)) break;
            start--;
            steps++;
        }
        var end = hitEnd;
        steps = 0;
        // 向后窗口比向前宽：中文是 SVO（"N1 完成了"，谓语紧跟代号），日/韩是 SOV，
        // 谓语在**句末**——"N1 관련해서 … 전부 구현 완료했습니다" 里 완료 离代号很远。
        // 24 字符在中文≈12 个字，在韩语只≈3~4 个어절，状态词常常正好被截掉。
        while (end < text.Length && steps < 48)
        {
            if (IsClauseBreak(text, end)) break;
            end++;
            steps++;
        }
        // 摘录进词典面板/chip popover 展示 → 过 Mining 档规整（仅行内 unwrap，
        // 不做块级 skip：窗口仅 ~44 字符，skip 会掏空）。状态推断吃的是本函数
        // 返回值，unwrap 只去标记不改语义关键词，不影响 InferStatus。
        return Text.TextNormalizer.Normalize(text[start..end], Text.NormalizeProfile.Mining);
    }

    // ── 状态识别词表（四语常开）
    //
    // 这些是**识别**词、不是展示文案，所以不进 design/strings.json：会话里出现哪种语言
    // 与界面语言无关（中文界面照样会读到日文 agent 输出），四张表必须同时生效。
    // mac 端 CodenameDetector 逐条镜像同一份表。
    //
    // 分档取舍：日/韩的「修正 / 수정」同时涵盖中文的"修改"与"修复"两义，而 Changed 档
    // 先于 Completed 判——放进 Changed 会让「バグを修正しました」「수정 완료」被记成"变更"。
    // 故日韩侧 Changed 只收无歧义的变更词，修复义归 Completed 的「修正済 / 수정 완료」。

    private static readonly string[] ChangedKeywords =
    {
        "变更", "调整", "改动", "修改", "重新设计",                        // zh
        "rework", "revised", "redesign",                                  // en
        "変更", "調整", "見直し", "差し替え", "方針転換",                   // ja
        "변경", "조정", "재설계", "개편",                                  // ko
    };

    private static readonly string[] CompletedKeywords =
    {
        "完成", "收口", "验收", "已实现", "搞定", "修复了",                 // zh
        "done", "closed", "finished", "resolved",                         // en
        "完了", "対応済", "実装済", "修正済", "解決",                       // ja（「完成」zh 表已覆盖）
        "완료", "완성", "해결", "마무리",                                  // ko
    };

    private static readonly string[] ActiveKeywords =
    {
        "开始", "执行", "推进", "继续", "进行中", "启动", "开展", "接下去", "接下来",  // zh
        "in progress", "working", "wip", "ongoing",                       // en
        "進行中", "対応中", "作業中", "実装中", "着手", "開始", "継続",     // ja
        "진행 중", "진행중", "작업 중", "작업중", "착수", "시작", "계속",   // ko
    };

    /// <summary>
    /// 中文前置否定：关键词**前两字符**内出现即忽略这次命中（"尚未完成"/"不执行"）。
    ///
    /// ⚠ 只对中文成立，**不要往里加日/韩的字**：
    /// · 韩语 <c>미</c>（未）看似同义，但 <c>이미 완료</c>（已经完成，真实语料 11,265 次）
    ///   里 <c>미</c> 正好落在窗口内——加进来会把最强的肯定句杀掉；韩语前置否定只能
    ///   按**词边界**判，见 <see cref="HasKoreanPrefixNegation"/>；
    /// · 日语里 <c>不/非/无/没</c> 是普通构词汉字（不具合 / 非表示）。实测这两例的
    ///   否定字都够不着两字窗口，所以现状不误伤——但也正因如此，别再往里加字。
    /// </summary>
    private static readonly HashSet<char> NegationChars = new("未没不别无非");

    /// <summary>
    /// 日/韩后置否定标记。日语谓语在句末、否定是词尾（完了して**いない**），
    /// 韩语同理（완료하**지 않**았다）——现有"前两字符"逻辑对它们完全够不着，
    /// 不补这条，「完了していない」会被当成"完成"记进词典。
    /// </summary>
    private static readonly string[] SuffixNegations =
    {
        "ない", "ありません", "ません", "なかった", "なし", "無い", "ず",   // ja
        "않", "못하", "못했", "없",                                       // ko
    };

    /// <summary>
    /// 后置否定的搜索窗口（字符上限）。日语侧实测的精度拐点：距离 1~8 字精度 85~100%，
    /// 再往后骤降——「かもしれない」「問題がない」这类与关键词无关的否定会大量涌入。
    /// 宁可漏，不可误杀。实际窗口还会被**子句边界**截断（见 <see cref="SuffixNegationEnd"/>），
    /// 邻句的否定与本次命中无关。
    /// </summary>
    private const int SuffixNegationWindow = 8;

    /// <summary>
    /// 整体是肯定语的固定搭配，含否定词但**不是**在否定关键词。
    /// 「問題ない」是评审通过、不是"没完成"。
    /// </summary>
    private static readonly string[] NegationWhitelist =
    {
        "問題ない", "問題ありません", "問題なし", "支障ない", "なくはない", "문제없", "문제 없",
    };

    public static CodenameStatus? InferStatus(string window)
    {
        if (ContainsKeyword(window, ChangedKeywords)) return CodenameStatus.Changed;
        if (ContainsKeyword(window, CompletedKeywords)) return CodenameStatus.Completed;
        if (ContainsKeyword(window, ActiveKeywords)) return CodenameStatus.Active;
        return null;
    }

    /// <summary>
    /// 命中了关键词、且这次命中**没有被否定**。
    ///
    /// 否定的**位置随语言不同**，所以三条判据并行（会话语言与界面语言无关，
    /// 故一律全开，不按设置切换）：
    ///   · 中文/英文前置——关键词前两字符内的 <see cref="NegationChars"/>；
    ///   · 韩语前置——按**词边界**判的 <c>안/못/미</c>，不能按字符（见 NegationChars 注释）；
    ///   · 日/韩后置——关键词后 <see cref="SuffixNegationWindow"/> 字符内的词尾否定。
    ///
    /// 匹配前做 NFKC 归一：日语全角英数（<c>ＷＩＰ</c>）、半角片假名、分离浊点
    /// 会让子串匹配整个失效，而这类输入在日语语料里很常见。
    /// </summary>
    private static bool ContainsKeyword(string window, string[] keywords)
    {
        var lower = Text.TextNormalizer.ForMatch(window);
        foreach (var keyword in keywords)
        {
            var searchStart = 0;
            while (searchStart <= lower.Length - keyword.Length)
            {
                var hit = lower.IndexOf(keyword, searchStart, StringComparison.Ordinal);
                if (hit < 0) break;
                var hitEnd = hit + keyword.Length;
                searchStart = hitEnd;
                if (!Text.TextNormalizer.HasWordBoundary(lower, keyword, hit, hitEnd)) continue;
                if (!IsNegated(lower, hit, hitEnd)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 匹配态兼容折叠（不影响展示文本）。见
    /// <see cref="Text.TextNormalizer.FoldForMatch"/> —— 那里写了为什么不用平台 NFKC。
    /// </summary>
    internal static string NormalizeForMatch(string text) => Text.TextNormalizer.FoldForMatch(text);

    private static bool IsNegated(string text, int hit, int hitEnd)
    {
        var tailEnd = SuffixNegationEnd(text, hitEnd);

        // 白名单优先：「問題ない」整体是肯定语，别被后置否定误杀。
        // 搜索范围要**覆盖整个待检区间**（前置窗口 ~ 后置窗口），只在关键词紧邻处找
        // 会够不着——「完成、問題ないです」里的 ない 落在关键词之后 4 字。
        foreach (var ok in NegationWhitelist)
        {
            var from = Math.Max(0, hit - 4 - ok.Length);
            var to = Math.Min(text.Length, tailEnd + ok.Length);
            if (text.AsSpan(from, to - from).Contains(ok, StringComparison.Ordinal)) return false;
        }

        for (var back = 1; back <= 2 && hit - back >= 0; back++)
        {
            if (NegationChars.Contains(text[hit - back])) return true;
        }
        if (HasKoreanPrefixNegation(text, hit)) return true;

        if (tailEnd > hitEnd)
        {
            var tail = text.AsSpan(hitEnd, tailEnd - hitEnd);
            foreach (var neg in SuffixNegations)
            {
                if (tail.Contains(neg, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 后置否定窗口的右界：<see cref="SuffixNegationWindow"/> 字符，且**遇子句边界即止**。
    /// 「完了した。ほかに問題がないか確認」——句号后的否定说的是另一件事。
    /// </summary>
    private static int SuffixNegationEnd(string text, int hitEnd)
    {
        var limit = Math.Min(text.Length, hitEnd + SuffixNegationWindow);
        for (var i = hitEnd; i < limit; i++)
        {
            if (IsClauseBreak(text, i)) return i;
        }
        return limit;
    }

    /// <summary>
    /// 韩语前置否定：<c>안</c>/<c>못</c> 必须是**独立어절**（两侧是空白或串界），
    /// <c>미</c> 必须**紧贴关键词且自身在词首**。
    ///
    /// 为什么不能按字符：真实语料里 <c>이미 완료</c>（已经完成，11,265 次）、
    /// <c>제안 완료</c>（提案完成，3,261 次）、<c>잘못</c>（84,805 次）都含这些字，
    /// 按字符判会把大量肯定句误杀。词边界一加，这三类全部正确放行。
    /// </summary>
    private static bool HasKoreanPrefixNegation(string text, int hit)
    {
        // 미완료 / 미적용 / 미반영：미 紧贴关键词，且它左边是词界
        if (hit >= 1 && text[hit - 1] == '미' && (hit == 1 || IsWordBoundary(text[hit - 2])))
        {
            return true;
        }
        // 안 / 못：独立어절，允许与关键词之间隔若干空白
        var i = hit - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i < 0) return false;
        var end = i + 1;
        while (i >= 0 && !char.IsWhiteSpace(text[i])) i--;
        var token = text[(i + 1)..end];
        return token is "안" or "못";
    }

    private static bool IsWordBoundary(char c) =>
        char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);
}
