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
    private static let br = regex(#"<br\s*/?>"#, options: [.caseInsensitive])

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
                line = replaceFirst(quotePrefix, in: line, with: "")
                line = replaceFirst(listPrefix, in: line, with: "")
            }

            out.append(line)
            first = false
        }
        return out.joined(separator: "\n")
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
            s = s.replacingOccurrences(
                of: sentinel + String(i) + sentinel, with: protectedSpans[i])
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
        _ pattern: String, options: NSRegularExpression.Options = []
    ) -> NSRegularExpression {
        // 规范内正则均在 ICU/.NET 共同子集内；编译失败属编程错误。
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

    private static func trimTrailing(_ s: String) -> String {
        var t = s
        while let last = t.last, last == " " || last == "\t" { t.removeLast() }
        return t
    }
}
