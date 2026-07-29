using System.Text;
using System.Text.RegularExpressions;

namespace AgentTimeline.Core.Text;

/// <summary>展示态文本规整的三个档位（docs/TEXT-NORMALIZATION.md §3.1）。</summary>
public enum NormalizeProfile
{
    /// <summary>结果行派生：全部规则，块级 skip 生效。</summary>
    Excerpt,

    /// <summary>规则摘要的标题/要点展示文本：围栏只保护不删除，行首列表前缀全剥。</summary>
    Summary,

    /// <summary>代号词典 lastContext：仅行内 unwrap（窗口 ~44 字符，块级 skip 会掏空）。</summary>
    Mining,
}

/// <summary>
/// 展示态文本规整（docs/TEXT-NORMALIZATION.md §3，v2 规则表经三方独立审查）。
///
/// 纯函数、无 IO、无状态——mac 端按同一份规范逐条移植，双端共用
/// docs/normalize-cases.tsv 作为 golden 验收基准。
///
/// **不作用于** nodes.text（命令原文）、TaskComplete.FullText（代号挖掘输入）、
/// SummaryJson.BuildPrompt（LLM 本就吃 markdown）——原文永不改写是产品底线。
///
/// 管线顺序即正确性（§3.2）：行尾归一 → ANSI strip → 逐行状态机(围栏/表格/水平线/
/// 标题) → 行内保护 → 行内变换(链接/图片/引用/强调) → 回填(verbatim) → 空行折叠。
/// </summary>
public static class TextNormalizer
{
    private const int ScanBudgetChars = 32 * 1024; // §3.4-2：实测 p99 6KB、max 38KB

    // ── 行内规则（均禁跨行：语料 29 处跨段误配）
    private static readonly Regex InlineCode = new(
        @"`([^`\n]+)`", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>图片先于链接消费，否则留下悬空 "!"。</summary>
    private static readonly Regex Image = new(
        @"!\[([^\]\n]*)\]\(([^)\n]*)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Link = new(
        @"\[([^\]\n]*)\]\(([^)\n]*)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>旧版 Codex 引用；判据收紧到 †L\d+ 以避开字面占位符。</summary>
    private static readonly Regex FileCitation = new(
        @"【F:([^†】\n]+)†L(\d+)(?:-L?\d+)?】", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 强调：两端非空白、禁跨行；glob(src/**/*.ts) 由紧邻 '/' 判定排除。
    private static readonly Regex StrongStar = new(
        @"(?<![\\/])\*\*(?=\S)([^\n]+?)(?<=\S)\*\*(?![\\/])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StrongUnderscore = new(
        @"__(?=\S)([^\n]+?)(?<=\S)__", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Strike = new(
        @"~~(?=\S)([^\n]+?)(?<=\S)~~", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Ansi = new(
        @"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Br = new(
        @"<br\s*/?>", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // ── 行级规则
    /// <summary>行首尾锚定；实测 20213 命中 / 孤立命中 0。宽松「含竖线即跳」会多杀 1599 行正文。</summary>
    private static readonly Regex TableRow = new(
        @"^[ \t]*\|.*\|[ \t]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>--- / *** / ___ 水平线；必须排在强调规则之前。</summary>
    private static readonly Regex HorizontalRule = new(
        @"^[ \t]{0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>ATX 标题：井号后必须有空格（#include/#!/#region 53 处不得误伤）。</summary>
    private static readonly Regex AtxHeading = new(
        @"^[ \t]{0,3}(#{1,6})[ \t]+(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FenceOpen = new(
        @"^([ \t]*)(```+|~~~+)[ \t]*([A-Za-z0-9_+-]*)[ \t]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ListPrefix = new(
        @"^[ \t]*(?:[-*+•·]|\d{1,3}[.)])[ \t]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuotePrefix = new(
        @"^[ \t]*>[ \t]?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>info string 为这些值时围栏是正文（如 ```text 包裹的任务书），不 skip。</summary>
    private static readonly HashSet<string> ProseFenceInfo =
        new(StringComparer.OrdinalIgnoreCase) { "", "text", "txt", "md", "markdown", "plain" };

    private const char Sentinel = '\uE000'; // 私用区，语料不会出现

    public static string Normalize(string? text, NormalizeProfile profile)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // 1) 行尾归一：仅 \r\n 与孤立 \r（枚举写死，双端才对得齐）
        var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (s.Length > ScanBudgetChars) s = s[..ScanBudgetChars];

        // 2) ANSI
        s = Ansi.Replace(s, "");
        s = Br.Replace(s, "\n");

        // 3) 逐行状态机（Mining 档跳过块级处理）
        s = profile == NormalizeProfile.Mining ? s : ProcessLines(s, profile);

        // 4~6) 行内保护 → 变换 → 回填
        s = ProcessInline(s);

        // 7) 空行折叠
        s = CollapseBlankLines(s).Trim();
        return s;
    }

    /// <summary>
    /// 块级：围栏（闭合才 skip，容差 +3，正文 info 不 skip）/ 表格 / 水平线 / 标题 / 列表前缀。
    /// 逐行状态机而非正则——无闭合围栏上的 [\s\S]*? 是 O(n²)。
    /// </summary>
    private static string ProcessLines(string s, NormalizeProfile profile)
    {
        var lines = s.Split('\n');
        var drop = new bool[lines.Length];

        // 围栏：先扫一遍标出「已闭合」的成对区间
        for (var i = 0; i < lines.Length; i++)
        {
            var open = FenceOpen.Match(lines[i]);
            if (!open.Success) continue;
            var indent = open.Groups[1].Value.Length;
            var marker = open.Groups[2].Value[0];
            var info = open.Groups[3].Value;

            var close = -1;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var m = FenceOpen.Match(lines[j]);
                if (!m.Success || m.Groups[2].Value[0] != marker) continue;
                if (m.Groups[1].Value.Length > indent + 3) continue; // 容差 ≤ 开围栏缩进+3
                if (m.Groups[3].Value.Length > 0) continue;          // 闭围栏不带 info
                close = j;
                break;
            }
            if (close < 0) continue;                       // 未闭合 → 按普通行（防吞到 EOF）

            // Excerpt 档才删；正文型 info（text/md/空）永远保留内容
            if (profile == NormalizeProfile.Excerpt && !ProseFenceInfo.Contains(info))
            {
                for (var k = i; k <= close; k++) drop[k] = true;
            }
            else
            {
                drop[i] = true;      // 只摘掉围栏标记行，内容留下
                drop[close] = true;
            }
            i = close;
        }

        var sb = new StringBuilder(s.Length);
        var first = true;
        for (var i = 0; i < lines.Length; i++)
        {
            if (drop[i]) continue;
            var line = lines[i].TrimEnd();
            if (HorizontalRule.IsMatch(line)) continue;
            if (profile == NormalizeProfile.Excerpt && TableRow.IsMatch(line)) continue;

            var heading = AtxHeading.Match(line);
            if (heading.Success) line = heading.Groups[2].Value.Trim();

            // Excerpt: 首行剥前缀（结果行已有 "→ " 渲染前缀）；Summary: 全剥
            var stripPrefix = profile == NormalizeProfile.Summary || first;
            if (stripPrefix) line = StripLeadingMarkers(line);

            if (!first) sb.Append('\n');
            sb.Append(line);
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 行内：保护 code → 链接/图片/引用/强调 → 回填（verbatim）。
    ///
    /// 链接先于 code 保护处理：真实语料里链接文字常含行内 code
    /// （`[这笔执行 \`153122\`](https://…)`），若先把 code 换成哨兵，
    /// 链接正则的 `[^\]\n]*` 仍能匹配（哨兵是普通字符），故顺序安全；
    /// 但哨兵必须不含 `]`/`)`——私用区字符满足。
    /// </summary>
    /// <summary>
    /// 剥掉**串首**的引用/列表标记（<c>&gt; </c> / <c>- </c> / <c>1. </c>），只作用于第一行
    /// （正则未开 Multiline，<c>^</c> 只锚定串首）。
    ///
    /// 规整管线内部与 <c>ParserUtil.ResultExcerpt</c> 的引子续接共用同一判据：
    /// 续接进来的段落首行还带着标记，规整层在 Excerpt 档只剥全文首行。
    /// 与 mac <c>TextNormalizer.stripLeadingMarkers</c> 同语义。
    /// </summary>
    public static string StripLeadingMarkers(string line)
        => ListPrefix.Replace(QuotePrefix.Replace(line, "", 1), "", 1);

    private static string ProcessInline(string s)
    {
        var protectedSpans = new List<string>();
        s = InlineCode.Replace(s, m =>
        {
            protectedSpans.Add(m.Groups[1].Value);
            return Sentinel + (protectedSpans.Count - 1).ToString() + Sentinel;
        });

        s = FileCitation.Replace(s, m => $"{m.Groups[1].Value}:{m.Groups[2].Value}");
        s = Image.Replace(s, m => m.Groups[1].Value);
        s = Link.Replace(s, m =>
            IsPathOrUrl(m.Groups[2].Value) ? m.Groups[1].Value : m.Value);

        s = StrongStar.Replace(s, "$1");
        s = StrongUnderscore.Replace(s, "$1");
        s = Strike.Replace(s, "$1");

        for (var i = protectedSpans.Count - 1; i >= 0; i--)
        {
            s = s.Replace(Sentinel + i.ToString() + Sentinel, protectedSpans[i]);
        }
        return s;
    }

    /// <summary>
    /// 链接 target 判定（v2，审查 reject 后重写）：先脱 &lt;…&gt; 包裹与 :line/#Lnnn 后缀，
    /// 再判路径或 http(s)。覆盖 /C:/… 前置斜杠盘符与 POSIX 绝对路径——旧版只认裸盘符，
    /// 漏判 13.6% 且在 mac 端全灭。Kimi 日志形态 (FuncName) 仍 100% 落在 keep 分支。
    /// </summary>
    internal static bool IsPathOrUrl(string target)
    {
        var t = target.Trim();
        if (t.StartsWith('<') && t.EndsWith('>')) t = t[1..^1].Trim();
        if (t.Length == 0) return false;

        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (t.StartsWith("./", StringComparison.Ordinal) ||
            t.StartsWith("../", StringComparison.Ordinal) ||
            t.StartsWith(".\\", StringComparison.Ordinal))
        {
            return true;
        }
        // 绝对路径：/x、C:\x、C:/x、/C:/x（Codex 新版 citation 主力形态）
        if (t[0] == '/' || t[0] == '\\')
        {
            if (t.Length > 3 && t[0] == '/' && char.IsLetter(t[1]) && t[2] == ':') return true;
            return true;
        }
        return t.Length > 2 && char.IsLetter(t[0]) && t[1] == ':' && (t[2] == '\\' || t[2] == '/');
    }

    private static string CollapseBlankLines(string s)
    {
        var sb = new StringBuilder(s.Length);
        var newlineRun = 0;
        foreach (var c in s)
        {
            if (c == '\n')
            {
                newlineRun++;
                if (newlineRun > 2) continue;
            }
            else
            {
                newlineRun = 0;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
