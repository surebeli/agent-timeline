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

    private static let clauseSeparators: Set<Character> = ["，", "。", "；", ";", ",", "、", "\n", "！", "？"]

    /// The clause containing the hit — status keywords from neighbouring clauses
    /// ("N3变更，N1 继续") must not bleed into this codename's window.
    private static func clause(around hit: Range<String.Index>, in text: String) -> String {
        var start = hit.lowerBound
        var steps = 0
        while start > text.startIndex, steps < 20 {
            let prev = text.index(before: start)
            if clauseSeparators.contains(text[prev]) { break }
            start = prev
            steps += 1
        }
        var end = hit.upperBound
        steps = 0
        while end < text.endIndex, steps < 24 {
            if clauseSeparators.contains(text[end]) { break }
            end = text.index(after: end)
            steps += 1
        }
        return String(text[start..<end])
    }

    private static let changedKeywords = ["变更", "调整", "改动", "修改", "重新设计", "rework"]
    private static let completedKeywords = ["完成", "收口", "验收", "已实现", "done", "closed", "finished", "搞定", "修复了"]
    private static let activeKeywords = ["开始", "执行", "推进", "继续", "进行中", "启动", "开展", "接下去", "接下来", "in progress", "working"]

    private static let negationChars: Set<Character> = ["未", "没", "不", "别", "无", "非"]

    private static func inferStatus(from window: String) -> CodenameStatus? {
        if containsKeyword(window, changedKeywords) { return .changed }
        if containsKeyword(window, completedKeywords) { return .completed }
        if containsKeyword(window, activeKeywords) { return .active }
        return nil
    }

    /// Keyword hit that is NOT negated — "尚未完成"/"不执行" must not record
    /// 完成/进行中. Negation = one of 未没不别无非 within the two characters
    /// immediately before the keyword.
    private static func containsKeyword(_ window: String, _ keywords: [String]) -> Bool {
        let lower = window.lowercased()
        for keyword in keywords {
            var searchStart = lower.startIndex
            while let hit = lower.range(of: keyword, range: searchStart..<lower.endIndex) {
                searchStart = hit.upperBound
                var negated = false
                var idx = hit.lowerBound
                for _ in 0..<2 where idx > lower.startIndex {
                    idx = lower.index(before: idx)
                    if negationChars.contains(lower[idx]) {
                        negated = true
                        break
                    }
                }
                if !negated { return true }
            }
        }
        return false
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
