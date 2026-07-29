import Foundation

/// 展示态文本规整的三个档位（docs/TEXT-NORMALIZATION.md §3.1）。
enum NormalizeProfile: Sendable {
    /// 结果行派生：全部规则，块级 skip 生效。
    case excerpt
    /// 规则摘要的标题/要点展示文本：围栏只保护不删除，行首列表前缀全剥。
    case summary
    /// 代号词典 lastContext：仅行内 unwrap（窗口 ~44 字符，块级 skip 会掏空）。
    case mining
}

/// 展示态文本规整（docs/TEXT-NORMALIZATION.md §3，v2 规则表经三方独立审查）。
///
/// 纯函数、无 IO、无状态——与 Windows `Core/Text/TextNormalizer.cs` 同规范实现，
/// 双端共用 `docs/normalize-cases.tsv` 作为 golden 验收基准。
///
/// **不作用于** `nodes.text`（命令原文）、代号挖掘的全文输入、LLM prompt——
/// 原文永不改写是产品底线。
///
/// 管线顺序即正确性（§3.2）：行尾归一 → ANSI strip → 逐行状态机（围栏/表格/
/// 水平线/标题） → 行内保护 → 行内变换（引用/图片/链接/强调） → 回填（verbatim）
/// → 空行折叠。
enum TextNormalizer {
    /// §3.4-2：实测 p99 6KB、max 38KB，无需为 100KB 设计。
    private static let scanBudget = 32 * 1024

    // ── 行内规则（均禁跨行：语料 29 处跨段误配）
    private static let inlineCode = regex(#"`([^`\n]+)`"#)
    /// 图片先于链接消费，否则留下悬空 "!"。
    private static let image = regex(#"!\[([^\]\n]*)\]\(([^)\n]*)\)"#)
    private static let link = regex(#"\[([^\]\n]*)\]\(([^)\n]*)\)"#)
    /// 旧版 Codex 引用；判据收紧到 †L\d+ 以避开字面占位符。
    private static let fileCitation = regex(#"【F:([^†】\n]+)†L(\d+)(?:-L?\d+)?】"#)
    /// 强调：两端非空白、禁跨行；glob(src/**/*.ts) 由紧邻 '/' 判定排除。
    private static let strongStar = regex(#"(?<![\\/])\*\*(?=\S)([^\n]+?)(?<=\S)\*\*(?![\\/])"#)
    private static let strongUnderscore = regex(#"__(?=\S)([^\n]+?)(?<=\S)__"#)
    private static let strike = regex(#"~~(?=\S)([^\n]+?)(?<=\S)~~"#)
    // ESC 必须由 Swift 字面量插入真实字符：ICU 不认 `\u{1B}` 这种带花括号的写法
    // （原始字符串会把它原样交给 ICU 而报 invalid pattern）。
    private static let ansi = regex("\u{1B}\\[[0-9;]*[A-Za-z]")
    private static let br = regex(
        #"<br\s*/?>"#, options: [.caseInsensitive, .useUnixLineSeparators])

    // ── 行级规则
    /// 行首尾锚定；实测 20213 命中 / 孤立命中 0。宽松「含竖线即跳」会多杀 1599 行正文。
    private static let tableRow = regex(#"^[ \t]*\|.*\|[ \t]*$"#)
    /// `---` / `***` / `___` 水平线；必须排在强调规则之前。
    private static let horizontalRule = regex(#"^[ \t]{0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$"#)
    /// ATX 标题：井号后必须有空格（#include / #! / #region 53 处不得误伤）。
    private static let atxHeading = regex(#"^[ \t]{0,3}(#{1,6})[ \t]+(.*)$"#)
    private static let fenceOpen = regex(#"^([ \t]*)(```+|~~~+)[ \t]*([A-Za-z0-9_+-]*)[ \t]*$"#)
    private static let listPrefix = regex(#"^[ \t]*(?:[-*+•·]|\d{1,3}[.)])[ \t]+"#)
    private static let quotePrefix = regex(#"^[ \t]*>[ \t]?"#)

    /// info string 为这些值时围栏是正文（如 ```text 包裹的任务书），不 skip。
    private static let proseFenceInfo: Set<String> = ["", "text", "txt", "md", "markdown", "plain"]

    /// 私用区，语料不会出现。
    private static let sentinel = "\u{E000}"

    static func normalize(_ text: String?, profile: NormalizeProfile) -> String {
        guard let text, !text.isEmpty else { return "" }

        // 1) 行尾归一：仅 \r\n 与孤立 \r（枚举写死，双端才对得齐）
        var s = text.replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
        if s.utf16.count > scanBudget {
            // 按 UTF-16 预算裁剪，但落在字符边界上（截断口径见 §3.4-4）
            let cutoff = String.Index(utf16Offset: scanBudget, in: s)
            s = String(s[s.startIndex..<cutoff])
        }

        // 2) ANSI / <br>
        s = replaceAll(ansi, in: s, with: "")
        s = replaceAll(br, in: s, with: "\n")

        // 3) 逐行状态机（Mining 档跳过块级处理）
        if profile != .mining {
            s = processLines(s, profile: profile)
        }

        // 4~6) 行内保护 → 变换 → 回填
        s = processInline(s)

        // 7) 空行折叠
        return collapseBlankLines(s).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // MARK: - 块级

    /// 围栏（闭合才 skip，容差 +3，正文 info 不 skip）/ 表格 / 水平线 / 标题 / 列表前缀。
    /// 逐行状态机而非正则——无闭合围栏上的 `[\s\S]*?` 是 O(n²)。
    private static func processLines(_ s: String, profile: NormalizeProfile) -> String {
        let lines = s.components(separatedBy: "\n")
        var drop = [Bool](repeating: false, count: lines.count)

        // 围栏：先扫一遍标出「已闭合」的成对区间
        var i = 0
        while i < lines.count {
            guard let open = match(fenceOpen, lines[i]) else { i += 1; continue }
            let indent = group(open, 1, in: lines[i]).count
            guard let markerChar = group(open, 2, in: lines[i]).first else { i += 1; continue }
            let info = group(open, 3, in: lines[i])

            var close = -1
            var j = i + 1
            while j < lines.count {
                if let m = match(fenceOpen, lines[j]),
                   group(m, 2, in: lines[j]).first == markerChar,
                   group(m, 1, in: lines[j]).count <= indent + 3,  // 容差 ≤ 开围栏缩进+3
                   group(m, 3, in: lines[j]).isEmpty {             // 闭围栏不带 info
                    close = j
                    break
                }
                j += 1
            }
            guard close >= 0 else { i += 1; continue }  // 未闭合 → 按普通行（防吞到 EOF）

            // Excerpt 档才删；正文型 info（text/md/空）永远保留内容
            if profile == .excerpt && !proseFenceInfo.contains(info.lowercased()) {
                for k in i...close { drop[k] = true }
            } else {
                drop[i] = true      // 只摘掉围栏标记行，内容留下
                drop[close] = true
            }
            i = close + 1
        }

        var out: [String] = []
        var first = true
        for idx in lines.indices where !drop[idx] {
            var line = trimTrailing(lines[idx])
            if match(horizontalRule, line) != nil { continue }
            if profile == .excerpt, match(tableRow, line) != nil { continue }

            if let heading = match(atxHeading, line) {
                line = group(heading, 2, in: line).trimmingCharacters(in: .whitespaces)
            }

            // Excerpt: 首行剥前缀（结果行已有 "→ " 渲染前缀）；Summary: 全剥
            if profile == .summary || first {
                line = stripLeadingMarkers(line)
            }

            out.append(line)
            first = false
        }
        return out.joined(separator: "\n")
    }

    /// 剥掉**串首**的引用/列表标记（`> ` / `- ` / `1. `），只作用于第一行
    /// （正则未开 `.anchorsMatchLines`，`^` 只锚定串首）。
    ///
    /// 规整管线内部与 `ParserSupport.resultExcerpt` 的引子续接共用同一判据：
    /// 续接进来的段落首行还带着标记，规整层在 excerpt 档只剥全文首行。
    static func stripLeadingMarkers(_ line: String) -> String {
        replaceFirst(listPrefix, in: replaceFirst(quotePrefix, in: line, with: ""), with: "")
    }

    // MARK: - 行内

    /// 保护 code → 引用/图片/链接/强调 → 回填（verbatim）。
    ///
    /// 链接在 code 保护之后处理：真实语料里链接文字常含行内 code
    /// （`[这笔执行 \`153122\`](https://…)`），哨兵是不含 `]`/`)` 的私用区字符，
    /// 链接正则的 `[^\]\n]*` 仍能匹配，故顺序安全。
    private static func processInline(_ input: String) -> String {
        var protectedSpans: [String] = []
        var s = replaceMatches(inlineCode, in: input) { text, m in
            protectedSpans.append(group(m, 1, in: text))
            return sentinel + String(protectedSpans.count - 1) + sentinel
        }

        s = replaceMatches(fileCitation, in: s) { text, m in
            "\(group(m, 1, in: text)):\(group(m, 2, in: text))"
        }
        s = replaceMatches(image, in: s) { text, m in group(m, 1, in: text) }
        s = replaceMatches(link, in: s) { text, m in
            isPathOrURL(group(m, 2, in: text))
                ? group(m, 1, in: text)
                : String(text[Range(m.range, in: text)!])
        }

        s = replaceAll(strongStar, in: s, with: "$1")
        s = replaceAll(strongUnderscore, in: s, with: "$1")
        s = replaceAll(strike, in: s, with: "$1")

        for i in stride(from: protectedSpans.count - 1, through: 0, by: -1) {
            // 必须 ordinal：Foundation 默认的正则等价搜索拒绝「结束位置落在字形簇
            // 中间」的匹配——闭合反引号后紧跟组合字符（U+0301/FE0F/20E3/肤色修饰…）
            // 时哨兵将永远回填不掉，私用区字符会写进 result_line 且永久留库。
            // win 的 string.Replace(String,String) 本就是 ordinal。
            s = s.replacingOccurrences(
                of: sentinel + String(i) + sentinel, with: protectedSpans[i],
                options: [.literal])
        }
        return s
    }

    /// 链接 target 判定（v2，审查 reject 后重写）：先脱去可选 `<…>` 包裹与
    /// `:line[:col]` / `#Lnnn` 后缀，再按路径或 http(s) 判定。覆盖 `/C:/…`
    /// 前置斜杠盘符与 POSIX 绝对路径；Kimi 日志形态 `(FuncName)` 落在 keep 分支。
    static func isPathOrURL(_ target: String) -> Bool {
        var t = target.trimmingCharacters(in: .whitespaces)
        if t.hasPrefix("<") && t.hasSuffix(">") {
            t = String(t.dropFirst().dropLast()).trimmingCharacters(in: .whitespaces)
        }
        guard let firstChar = t.first else { return false }

        let lower = t.lowercased()
        if lower.hasPrefix("http://") || lower.hasPrefix("https://") || lower.hasPrefix("file:") {
            return true
        }
        if t.hasPrefix("./") || t.hasPrefix("../") || t.hasPrefix(".\\") { return true }
        // 绝对路径：/x、C:\x、C:/x、/C:/x（Codex 新版 citation 主力形态）
        if firstChar == "/" || firstChar == "\\" { return true }
        let chars = Array(t)
        return chars.count > 2 && chars[0].isLetter && chars[1] == ":"
            && (chars[2] == "\\" || chars[2] == "/")
    }

    private static func collapseBlankLines(_ s: String) -> String {
        var out = ""
        out.reserveCapacity(s.count)
        var newlineRun = 0
        for c in s {
            if c == "\n" {
                newlineRun += 1
                if newlineRun > 2 { continue }
            } else {
                newlineRun = 0
            }
            out.append(c)
        }
        return out
    }

    // MARK: - NSRegularExpression 薄封装

    private static func regex(
        _ pattern: String, options: NSRegularExpression.Options = [.useUnixLineSeparators]
    ) -> NSRegularExpression {
        // 规范内正则均在 ICU/.NET 共同子集内；编译失败属编程错误。
        // useUnixLineSeparators（ICU UREGEX_UNIX_LINES）是双端对齐的必要项：
        // ICU 默认把 U+000B/000C/0085/2028/2029 也当行终止符，`.` 不跨、`$` 会在
        // 其前锚定，而 .NET 只认 \n。§3.2-1 又刻意保留这些字符不做归一，
        // 不开这个开关时 ATX 标题与表格规则会在含这些字符的行上分叉。
        try! NSRegularExpression(pattern: pattern, options: options)
    }

    private static func match(_ re: NSRegularExpression, _ s: String) -> NSTextCheckingResult? {
        re.firstMatch(in: s, range: NSRange(s.startIndex..., in: s))
    }

    private static func group(_ m: NSTextCheckingResult, _ index: Int, in s: String) -> String {
        guard index < m.numberOfRanges, let r = Range(m.range(at: index), in: s) else { return "" }
        return String(s[r])
    }

    private static func replaceAll(
        _ re: NSRegularExpression, in s: String, with template: String
    ) -> String {
        re.stringByReplacingMatches(
            in: s, range: NSRange(s.startIndex..., in: s), withTemplate: template)
    }

    private static func replaceFirst(
        _ re: NSRegularExpression, in s: String, with template: String
    ) -> String {
        guard let m = match(re, s), let r = Range(m.range, in: s) else { return s }
        return s.replacingCharacters(in: r, with: template)
    }

    /// 逐匹配替换（需要按捕获组做条件判断时用）。正向拼接：所有索引都取自原串，
    /// 不存在跨字符串索引复用问题。
    private static func replaceMatches(
        _ re: NSRegularExpression, in s: String,
        _ transform: (String, NSTextCheckingResult) -> String
    ) -> String {
        let matches = re.matches(in: s, range: NSRange(s.startIndex..., in: s))
        guard !matches.isEmpty else { return s }
        var result = ""
        result.reserveCapacity(s.count)
        var cursor = s.startIndex
        for m in matches {
            guard let r = Range(m.range, in: s), r.lowerBound >= cursor else { continue }
            result.append(contentsOf: s[cursor..<r.lowerBound])
            result.append(transform(s, m))
            cursor = r.upperBound
        }
        result.append(contentsOf: s[cursor...])
        return result
    }

    /// 逐行 TrimEnd（§3.3）。必须覆盖全部 Unicode 空白而不只是空格/制表符——
    /// win 用 .NET `TrimEnd()`（char.IsWhiteSpace 全集）。中文语料里的全角空格
    /// U+3000 若不剥，会让"空行"不再是空行（首段边界整体错位）、让表格行与水平线
    /// 的行尾锚定失配而漏 skip。
    private static func trimTrailing(_ s: String) -> String {
        var t = Substring(s)
        while let last = t.last, last.isWhitespace { t = t.dropLast() }
        return String(t)
    }

    // MARK: - 匹配态兼容折叠（docs/TEXT-NORMALIZATION.md §3.6）
    //
    // 只服务**关键词子串匹配**（状态推断、分类词表），不作用于任何展示文本。
    //
    // ⚠️ **不要**改用 `precomposedStringWithCompatibilityMapping`（平台 NFKC）：
    //   1. .NET 侧 `String.Normalize(FormKC)` 在 `InvariantGlobalization=true` 下
    //      **静默原样返回**——不抛异常。win 的 CoreSmokeTest 正是这个配置、主程序不是，
    //      照搬会让「门禁跑的语义」与「线上跑的语义」不同，且无声；
    //   2. 平台 NFKC 各自绑 ICU 版本，mac 与 Windows 的 ICU 并不同版。双端对齐靠
    //      「各自调各自的 NFKC」反而不牢，靠这张写死的表才逐字节可复现。
    //
    // 覆盖面按真实需要划定（日语语料实测）：全角英数、表意空格、半角片假名、
    // 分离浊点/半浊点。圈号数字、合字、上下标这些 NFKC 也管的，与关键词匹配无关，不做。

    /// 半角片假名 U+FF61…U+FF9F → 全角，按 `scalar - 0xFF61` 直接索引。
    private static let halfwidthKatakana = Array(
        "。「」、・ヲァィゥェォャュョッーアイウエオカキクケコサシスセソタチツテト"
        + "ナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン゛゜")

    /// 浊点可合成的假名（片/平假名同表；合成值为 base + 1）。
    private static let voiceable = Set(
        "カキクケコサシスセソタチツテトハヒフヘホかきくけこさしすせそたちつてとはひふへほ".unicodeScalars)

    /// 半浊点可合成的假名（合成值为 base + 2）。
    private static let semiVoiceable = Set("ハヒフヘホはひふへほ".unicodeScalars)

    /// 关键词匹配前的兼容折叠。`ＷＩＰ`→`WIP`、`ﾃﾞﾌﾟﾛｲ`→`デプロイ`、表意空格→半角空格。
    /// 纯函数，输入不含上述形态时原样返回。与 win `TextNormalizer.FoldForMatch` 同表。
    static func foldForMatch(_ text: String) -> String {
        guard !text.isEmpty else { return text }
        var out = String.UnicodeScalarView()
        for scalar in text.unicodeScalars {
            var folded = scalar
            if scalar == "\u{3000}" {                                   // 表意空格
                folded = " "
            } else if scalar.value >= 0xFF01 && scalar.value <= 0xFF5E { // 全角英数记号
                folded = Unicode.Scalar(scalar.value - 0xFEE0)!
            } else if scalar.value >= 0xFF61 && scalar.value <= 0xFF9F { // 半角片假名
                folded = halfwidthKatakana[Int(scalar.value - 0xFF61)].unicodeScalars.first!
            }
            // 浊点/半浊点跟在可合成假名后面时就地合成；合不成就原样留着（保持可见）。
            // U+3099/U+309A 是 NFD 的组合记号，゛/゜(U+309B/309C) 是独立字符，
            // 半角 ﾞ/ﾟ 上一步已折成后者——三种形态在这里统一处理。
            if folded == "\u{3099}" || folded == "\u{309B}", let prev = out.last {
                if prev == "ウ" { out.removeLast(); out.append("ヴ"); continue }
                if voiceable.contains(prev) {
                    out.removeLast(); out.append(Unicode.Scalar(prev.value + 1)!); continue
                }
            } else if folded == "\u{309A}" || folded == "\u{309C}", let prev = out.last {
                if semiVoiceable.contains(prev) {
                    out.removeLast(); out.append(Unicode.Scalar(prev.value + 2)!); continue
                }
            }
            out.append(folded)
        }
        return String(out)
    }

    /// 折叠 + 小写，关键词匹配的统一入口态。状态词表与分类词表共用，
    /// 保证两处对同一段文本看到的是同一个形态。
    static func forMatch(_ text: String) -> String { foldForMatch(text).lowercased() }

    private static func isASCIIAlnum(_ s: Unicode.Scalar) -> Bool {
        (s.value >= 48 && s.value <= 57) || (s.value >= 65 && s.value <= 90)
            || (s.value >= 97 && s.value <= 122)
    }

    /// 拉丁关键词必须落在**词边界**上；CJK 关键词不设边界（`N2完成`、`バグ修正` 是自然写法）。
    ///
    /// 不加这条，纯子串匹配会误命中一批极常见的词：`prefix`/`suffix` 含 fix、
    /// `networking` 含 working、`disclosed` 含 closed、`swipe` 含 wip。
    /// 判据只看关键词两端**是否紧邻拉丁字母/数字**——`in progress.` 这类带标点的照常命中。
    static func hasWordBoundary(
        _ text: [Unicode.Scalar], _ keyword: [Unicode.Scalar], _ hit: Int, _ hitEnd: Int
    ) -> Bool {
        guard let first = keyword.first, let last = keyword.last else { return false }
        if !isASCIIAlnum(first) && !isASCIIAlnum(last) { return true }
        if hit > 0 && isASCIIAlnum(text[hit - 1]) { return false }
        if hitEnd < text.count && isASCIIAlnum(text[hitEnd]) { return false }
        return true
    }

    /// 词边界意义上的包含判定。`text` 需已过 `forMatch`。
    static func containsKeyword(_ text: String, _ keyword: String) -> Bool {
        containsKeyword(Array(text.unicodeScalars), Array(keyword.unicodeScalars))
    }

    /// 标量数组版：调用方已切好数组时避免重复转换（词表逐条比对时热路径）。
    static func containsKeyword(_ text: [Unicode.Scalar], _ keyword: [Unicode.Scalar]) -> Bool {
        guard !keyword.isEmpty, text.count >= keyword.count else { return false }
        var from = 0
        while from <= text.count - keyword.count {
            guard let hit = indexOf(text, keyword, from: from) else { return false }
            if hasWordBoundary(text, keyword, hit, hit + keyword.count) { return true }
            from = hit + 1
        }
        return false
    }

    private static func indexOf(
        _ text: [Unicode.Scalar], _ keyword: [Unicode.Scalar], from: Int
    ) -> Int? {
        guard !keyword.isEmpty else { return nil }
        var i = from
        while i <= text.count - keyword.count {
            var j = 0
            while j < keyword.count && text[i + j] == keyword[j] { j += 1 }
            if j == keyword.count { return i }
            i += 1
        }
        return nil
    }
}
