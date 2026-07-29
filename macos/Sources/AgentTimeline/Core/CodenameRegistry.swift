import Foundation

enum CodenameStatus: String, Codable, Sendable, CaseIterable {
    case defined = "定义"
    case active = "进行中"
    case completed = "完成"
    case changed = "变更"
    case mentioned = "提及"
}

/// Codename detection. Two safe channels into the dictionary:
/// 1) dash-style long codenames ("T-PLUGIN-00") match anywhere;
/// 2) short batch codes ("N1"/"T2") are too ambiguous for bare matching — they
///    enter only via a definition pattern ("N1: 登录改版"), and afterwards
///    known-code exact matches count as mentions/status updates.
enum CodenameDetector {
    private static let dashRegex = try! NSRegularExpression(
        pattern: #"\b[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,12}){1,3}\b"#)

    /// "N1: xxx" / "**N1**: xxx" / "编号如下：N1: 登录, N2: 支付" — a codename
    /// being (re)defined. Lead-in accepts colons/commas/whitespace so inline and
    /// replay-flattened lists work; the body stops before the next inline
    /// "CODE:" and at clause separators, so chained lists yield every code.
    private static let definitionRegex = try! NSRegularExpression(
        pattern: #"(?:^|[，。；;、,\n：:\s])[\s\-*•·>）)\d.]{0,8}\*{0,2}([A-Z]{1,4}\d{1,3}|[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,12}){1,3})\*{0,2}\s*[:：]\s*((?:(?!\s[\s\-*•·>）)\d.]{0,8}\*{0,2}(?:[A-Z]{1,4}\d{1,3}|[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,12}){1,3})\*{0,2}\s*[:：])[^\n，。；;、,]){2,80})"#,
        options: [.anchorsMatchLines])

    /// Tokens that look like codenames but never are — compared dash/dot-stripped
    /// so "HTTP-2"/"HTTP2" both hit, and stocked with tech/planning vocabulary
    /// that the short-code definition pattern would otherwise admit (S3/EC2/Q1…).
    private static let stopList: Set<String> = [
        "UTF8", "UTF16", "UTF32", "ISO8601", "SHA256", "SHA1", "MD5",
        "HTTP2", "HTTP3", "TLS1", "OAUTH2", "OAUTH20", "BASE64",
        "GPT4", "GPT5", "JSONRPC", "GRPCWEB", "XY", "AB", "QA",
        "S3", "EC2", "R2", "B2", "K8", "X86", "X64", "I18N", "L10N",
        "V1", "V2", "V3", "V4", "V5", "Q1", "Q2", "Q3", "Q4", "H1", "H2",
        "P0", "P1", "P2", "MP3", "MP4",
    ]

    /// Sanity gate for LLM-extracted names — the model occasionally emits list
    /// indices ("1") or punctuation as "codenames".
    static func isPlausibleName(_ name: String) -> Bool {
        name.count >= 2 && name.count <= 24
            && name.rangeOfCharacter(from: .letters) != nil
            && !isStopped(name)
    }

    static func isStopped(_ name: String) -> Bool {
        let normalized = name
            .replacingOccurrences(of: "-", with: "")
            .replacingOccurrences(of: ".", with: "")
            .uppercased()
        return stopList.contains(normalized)
    }

    /// Dash-style codenames mentioned anywhere in the text.
    static func detect(in text: String) -> [String] {
        let range = NSRange(text.startIndex..., in: text)
        var seen = Set<String>()
        var out: [String] = []
        for match in dashRegex.matches(in: text, range: range) {
            guard let r = Range(match.range, in: text) else { continue }
            let name = String(text[r])
            guard !isStopped(name), !seen.contains(name) else { continue }
            // Too-short tokens like "M-1"/"A-B" are noise; real codenames carry
            // either a digit ("T-PLUGIN-00") or enough length ("FEAT-LOGIN").
            guard name.count >= 4,
                  name.rangeOfCharacter(from: .decimalDigits) != nil || name.count >= 5
            else { continue }
            seen.insert(name)
            out.append(name)
        }
        return out
    }

    /// Codenames being defined in this text ("N1: xxx"), short codes included.
    static func detectDefinitions(in text: String) -> [(name: String, definition: String)] {
        let range = NSRange(text.startIndex..., in: text)
        var out: [(String, String)] = []
        var seen = Set<String>()
        for match in definitionRegex.matches(in: text, range: range) {
            guard let nameRange = Range(match.range(at: 1), in: text),
                  let defRange = Range(match.range(at: 2), in: text) else { continue }
            let name = String(text[nameRange])
            let definition = String(text[defRange]).trimmingCharacters(in: .whitespaces)
            guard !isStopped(name), seen.insert(name).inserted,
                  !definition.isEmpty else { continue }
            out.append((name, definition))
        }
        return out
    }

    /// Exact word-boundary occurrences of already-known codenames, with a status
    /// inferred from the surrounding context window.
    static func detectMentions(
        in text: String, known: Set<String>
    ) -> [(name: String, status: CodenameStatus?, context: String)] {
        guard !known.isEmpty else { return [] }
        var out: [(String, CodenameStatus?, String)] = []
        for name in known {
            var searchStart = text.startIndex
            var found: (CodenameStatus?, String)?
            while let hit = text.range(of: name, range: searchStart..<text.endIndex) {
                searchStart = hit.upperBound
                // Word boundary against ASCII alnum only — "T1" inside "T12"/"AT1"
                // is a different token, but CJK abutting ("N2完成") is natural.
                if hit.lowerBound > text.startIndex {
                    let prev = text[text.index(before: hit.lowerBound)]
                    if prev.isASCII && (prev.isLetter || prev.isNumber) { continue }
                }
                if hit.upperBound < text.endIndex {
                    let next = text[hit.upperBound]
                    if next.isASCII && (next.isLetter || next.isNumber) { continue }
                }
                let window = clause(around: hit, in: text)
                let status = inferStatus(from: window)
                // Prefer the occurrence that carries a status signal.
                if found == nil || (found?.0 == nil && status != nil) {
                    found = (status, window.replacingOccurrences(of: "\n", with: " "))
                }
                if status != nil { break }
            }
            if let (status, context) = found {
                out.append((name, status, context))
            }
        }
        return out
    }

    private static let clauseSeparators: Set<Character> = [
        "，", "。", "；", ";", ",", "、", "\n", "！", "？", "·",
    ]

    /// 该位置是否是分句点。
    ///
    /// ASCII `.` `!` `?` 只在**句末形态**（后面是空白或串尾）时才算——韩语句子 100% 用
    /// ASCII 句点收尾，不认它的话子句窗口永远不会在句号处切断、只会撞上长度上限，
    /// 邻句的状态词会大量串味（日/韩术语调研实测发现）。但不能无条件认：
    /// `v0.6.0` / `a.txt` / `1.5` 里的点会把窗口从中间截断。
    /// 与 win `CodenameDetector.IsClauseBreak` 同判据。
    private static func isClauseBreak(_ c: Character, next: Character?) -> Bool {
        if clauseSeparators.contains(c) { return true }
        if c == "." || c == "!" || c == "?" {
            guard let next else { return true }
            return next.isWhitespace
        }
        return false
    }

    /// The clause containing the hit — status keywords from neighbouring clauses
    /// ("N3变更，N1 继续") must not bleed into this codename's window.
    private static func clause(around hit: Range<String.Index>, in text: String) -> String {
        var start = hit.lowerBound
        var steps = 0
        while start > text.startIndex, steps < 20 {
            let prev = text.index(before: start)
            if isClauseBreak(text[prev], next: start < text.endIndex ? text[start] : nil) { break }
            start = prev
            steps += 1
        }
        var end = hit.upperBound
        steps = 0
        // 向后窗口比向前宽：中文是 SVO（"N1 完成了"，谓语紧跟代号），日/韩是 SOV，
        // 谓语在**句末**——「N1 관련해서 … 전부 구현 완료했습니다」里 완료 离代号很远。
        // 24 字符在中文≈12 个字，在韩语只≈3~4 个어절，状态词常常正好被截掉。
        while end < text.endIndex, steps < 48 {
            let after = text.index(after: end)
            if isClauseBreak(text[end], next: after < text.endIndex ? text[after] : nil) { break }
            end = after
            steps += 1
        }
        // 摘录进词典面板 / chip popover 展示 → 过 Mining 档规整（仅行内 unwrap，
        // 不做块级 skip：窗口仅 ~44 字符，skip 会掏空）。状态推断吃的是本函数
        // 返回值，unwrap 只去标记不改语义关键词，不影响 inferStatus。
        return TextNormalizer.normalize(String(text[start..<end]), profile: .mining)
    }

    // ── 状态识别词表（四语常开，docs/TEXT-NORMALIZATION.md §3.6）
    //
    // 这些是**识别**词、不是展示文案，所以不进 design/strings.json：会话里出现哪种语言
    // 与界面语言无关（中文界面照样会读到日文 agent 输出），四张表必须同时生效。
    // 与 win `CodenameDetector` 逐条同表。
    //
    // 分档取舍：日/韩的「修正 / 수정」同时涵盖中文的"修改"与"修复"两义，而 changed 档
    // 先于 completed 判——放进 changed 会让「バグを修正しました」「수정 완료」被记成"变更"。
    // 故日韩侧 changed 只收无歧义的变更词，修复义归 completed 的「修正済 / 수정 완료」。

    private static let changedKeywords = [
        "变更", "调整", "改动", "修改", "重新设计",                        // zh
        "rework", "revised", "redesign",                                  // en
        "変更", "調整", "見直し", "差し替え", "方針転換",                   // ja
        "변경", "조정", "재설계", "개편",                                  // ko
    ]

    private static let completedKeywords = [
        "完成", "收口", "验收", "已实现", "搞定", "修复了",                 // zh
        "done", "closed", "finished", "resolved",                         // en
        "完了", "対応済", "実装済", "修正済", "解決",                       // ja（「完成」zh 表已覆盖）
        "완료", "완성", "해결", "마무리",                                  // ko
    ]

    private static let activeKeywords = [
        "开始", "执行", "推进", "继续", "进行中", "启动", "开展", "接下去", "接下来",  // zh
        "in progress", "working", "wip", "ongoing",                       // en
        "進行中", "対応中", "作業中", "実装中", "着手", "開始", "継続",     // ja
        "진행 중", "진행중", "작업 중", "작업중", "착수", "시작", "계속",   // ko
    ]

    /// 中文前置否定：关键词**前两字符**内出现即忽略这次命中（"尚未完成"/"不执行"）。
    ///
    /// ⚠️ 只对中文成立，**不要往里加日/韩的字**：
    /// · 韩语 `미`（未）看似同义，但 `이미 완료`（已经完成，真实语料 11,265 次）里 `미`
    ///   正好落在窗口内——加进来会把最强的肯定句杀掉；韩语前置否定只能按**词边界**判，
    ///   见 `hasKoreanPrefixNegation`；
    /// · 日语里 `不/非/无/没` 是普通构词汉字（不具合 / 非表示）。实测这两例的否定字都
    ///   够不着两字窗口，所以现状不误伤——但也正因如此，别再往里加字。
    private static let negationChars: Set<Character> = ["未", "没", "不", "别", "无", "非"]

    /// 日/韩后置否定标记。日语谓语在句末、否定是词尾（完了して**いない**），
    /// 韩语同理（완료하**지 않**았다）——「前两字符」逻辑对它们完全够不着，
    /// 不补这条，「完了していない」会被当成"完成"记进词典。
    private static let suffixNegations = [
        "ない", "ありません", "ません", "なかった", "なし", "無い", "ず",   // ja
        "않", "못하", "못했", "없",                                       // ko
    ]

    /// 后置否定的搜索窗口（字符上限）。日语侧实测的精度拐点：距离 1~8 字精度 85~100%，
    /// 再往后骤降——「かもしれない」「問題がない」这类与关键词无关的否定会大量涌入。
    /// 宁可漏，不可误杀。实际窗口还会被**子句边界**截断：邻句的否定与本次命中无关。
    private static let suffixNegationWindow = 8

    /// 整体是肯定语的固定搭配，含否定词但**不是**在否定关键词。
    /// 「問題ない」是评审通过、不是"没完成"。
    private static let negationWhitelist = [
        "問題ない", "問題ありません", "問題なし", "支障ない", "なくはない", "문제없", "문제 없",
    ]

    private static func inferStatus(from window: String) -> CodenameStatus? {
        if containsKeyword(window, changedKeywords) { return .changed }
        if containsKeyword(window, completedKeywords) { return .completed }
        if containsKeyword(window, activeKeywords) { return .active }
        return nil
    }

    /// 命中了关键词、且这次命中**没有被否定**。
    ///
    /// 否定的**位置随语言不同**，所以三条判据并行（会话语言与界面语言无关，
    /// 故一律全开，不按设置切换）：
    ///   · 中文/英文前置——关键词前两字符内的 `negationChars`；
    ///   · 韩语前置——按**词边界**判的 `안/못/미`，不能按字符（见 negationChars 注释）；
    ///   · 日/韩后置——关键词后 `suffixNegationWindow` 字符内的词尾否定。
    ///
    /// 匹配前过 `TextNormalizer.forMatch` 做兼容折叠 + 小写（§3.6）：日语全角英数
    /// `ＷＩＰ`、半角片假名、分离浊点会让子串匹配整个失效，而这类输入在日语语料里很常见。
    /// 拉丁关键词另要求**词边界**——否则 `prefix` 会命中 `fix`。
    private static func containsKeyword(_ window: String, _ keywords: [String]) -> Bool {
        let lower = Array(TextNormalizer.forMatch(window).unicodeScalars)
        for keyword in keywords {
            let kw = Array(TextNormalizer.forMatch(keyword).unicodeScalars)
            guard !kw.isEmpty else { continue }
            var searchStart = 0
            while let hit = TextNormalizer.firstIndex(of: kw, in: lower, from: searchStart) {
                let hitEnd = hit + kw.count
                searchStart = hitEnd
                guard TextNormalizer.hasWordBoundary(lower, kw, hit, hitEnd) else { continue }
                if !isNegated(lower, hit, hitEnd) { return true }
            }
        }
        return false
    }

    private static func isNegated(_ text: [Unicode.Scalar], _ hit: Int, _ hitEnd: Int) -> Bool {
        let tailEnd = suffixNegationEnd(text, hitEnd)

        // 白名单优先：「問題ない」整体是肯定语，别被后置否定误杀。
        // 搜索范围要**覆盖整个待检区间**（前置窗口 ~ 后置窗口），只在关键词紧邻处找
        // 会够不着——「完成、問題ないです」里的 ない 落在关键词之后 4 字。
        for ok in negationWhitelist {
            let okScalars = Array(ok.unicodeScalars)
            let from = max(0, hit - 4 - okScalars.count)
            let to = min(text.count, tailEnd + okScalars.count)
            if from < to,
               TextNormalizer.firstIndex(of: okScalars, in: Array(text[from..<to]), from: 0) != nil {
                return false
            }
        }

        var back = 1
        while back <= 2 && hit - back >= 0 {
            if negationChars.contains(Character(text[hit - back])) { return true }
            back += 1
        }
        if hasKoreanPrefixNegation(text, hit) { return true }

        if tailEnd > hitEnd {
            let tail = Array(text[hitEnd..<tailEnd])
            for neg in suffixNegations
            where TextNormalizer.firstIndex(of: Array(neg.unicodeScalars), in: tail, from: 0) != nil {
                return true
            }
        }
        return false
    }

    /// 后置否定窗口的右界：`suffixNegationWindow` 字符，且**遇子句边界即止**。
    /// 「完了した。ほかに問題がないか確認」——句号后的否定说的是另一件事。
    private static func suffixNegationEnd(_ text: [Unicode.Scalar], _ hitEnd: Int) -> Int {
        let limit = min(text.count, hitEnd + suffixNegationWindow)
        for i in hitEnd..<limit {
            let next = i + 1 < text.count ? Character(text[i + 1]) : nil
            if isClauseBreak(Character(text[i]), next: next) { return i }
        }
        return limit
    }

    /// 韩语前置否定：`안`/`못` 必须是**独立어절**（两侧是空白或串界），
    /// `미` 必须**紧贴关键词且自身在词首**。
    ///
    /// 为什么不能按字符：真实语料里 `이미 완료`（已经完成，11,265 次）、
    /// `제안 완료`（提案完成，3,261 次）、`잘못`（84,805 次）都含这些字，
    /// 按字符判会把大量肯定句误杀。词边界一加，这三类全部正确放行。
    private static func hasKoreanPrefixNegation(_ text: [Unicode.Scalar], _ hit: Int) -> Bool {
        // 미완료 / 미적용 / 미반영：미 紧贴关键词，且它左边是词界
        if hit >= 1, Character(text[hit - 1]) == "미",
           hit == 1 || isWordBoundary(Character(text[hit - 2])) {
            return true
        }
        // 안 / 못：独立어절，允许与关键词之间隔若干空白
        var i = hit - 1
        while i >= 0 && Character(text[i]).isWhitespace { i -= 1 }
        guard i >= 0 else { return false }
        let end = i + 1
        while i >= 0 && !Character(text[i]).isWhitespace { i -= 1 }
        let token = String(String.UnicodeScalarView(text[(i + 1)..<end]))
        return token == "안" || token == "못"
    }

    private static func isWordBoundary(_ c: Character) -> Bool {
        c.isWhitespace || c.isPunctuation || c.isSymbol
    }
}

/// Persists codename sightings into the store, merging all three sources:
/// rule-based text mining (user commands AND agent replies), and LLM extraction.
struct CodenameRegistry: Sendable {
    let store: Store

    /// Mine a user command: definitions, dash-style mentions, then status updates
    /// against everything known (including codes just defined).
    func processCommand(_ cmd: UserCommand) {
        process(text: cmd.text, nodeId: cmd.id, at: cmd.timestamp)
    }

    /// Mine agent output — definitions frequently live in the reply ("好的，编号
    /// 如下：N1: …"). `nodeId` is the command node the reply belongs to.
    func processAssistantText(_ text: String, nodeId: String, at: Date) {
        process(text: text, nodeId: nodeId, at: at)
    }

    private func process(text: String, nodeId: String, at: Date) {
        var known = Set(store.fetchCodenames().keys)
        var definedNow = Set<String>()
        var bornNow = Set<String>()

        for (name, definition) in CodenameDetector.detectDefinitions(in: text) {
            store.defineCodename(name: name, definition: definition, nodeId: nodeId, at: at)
            definedNow.insert(name)
            known.insert(name)
        }
        for name in CodenameDetector.detect(in: text) where !known.contains(name) {
            store.recordCodename(name: name, definition: "", nodeId: nodeId, seenAt: at)
            known.insert(name)
            bornNow.insert(name)
        }
        // A definition sentence is not a status update about itself — keywords in
        // the definition body ("N1: 完成支付重构") must not flip the fresh 定义
        // status, and defineCodename already counted the occurrence.
        let mentionTargets = known.subtracting(definedNow)
        for (name, status, context) in CodenameDetector.detectMentions(in: text, known: mentionTargets) {
            store.touchCodename(
                name: name, status: status, context: context, nodeId: nodeId, at: at,
                bumpOccurrence: !bornNow.contains(name))
        }
    }

    func recordFromSummary(_ summary: Summary, nodeId: String, seenAt: Date) {
        for def in summary.codenames {
            let name = def.name.trimmingCharacters(in: .whitespacesAndNewlines)
            guard CodenameDetector.isPlausibleName(name) else { continue }
            store.recordCodename(name: name, definition: def.definition, nodeId: nodeId, seenAt: seenAt)
            if let statusRaw = def.status, let status = CodenameStatus(rawValue: statusRaw),
               status != .defined, status != .mentioned {
                store.touchCodename(name: name, status: status, context: "", nodeId: nodeId, at: seenAt)
            }
        }
    }
}
