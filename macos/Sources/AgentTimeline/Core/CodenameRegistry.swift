import Foundation

/// Detects task/requirement codenames like "T-PLUGIN-00", "REQ-AUTH-3" in command text.
/// Regex hits are unioned with LLM-extracted codenames; the first sighting becomes
/// the dictionary definition entry.
enum CodenameDetector {
    private static let regex = try! NSRegularExpression(
        pattern: #"\b[A-Z][A-Z0-9]{0,9}(?:-[A-Z0-9]{1,12}){1,3}\b"#)

    /// Common all-caps tokens that look like codenames but never are.
    private static let stopList: Set<String> = [
        "UTF-8", "UTF-16", "ISO-8601", "SHA-256", "SHA-1", "MD-5",
        "HTTP-2", "TLS-1", "OAUTH-2", "BASE-64", "GPT-4", "GPT-5",
        "X-Y", "A-B", "Q-A",
    ]

    static func detect(in text: String) -> [String] {
        let range = NSRange(text.startIndex..., in: text)
        var seen = Set<String>()
        var out: [String] = []
        for match in regex.matches(in: text, range: range) {
            guard let r = Range(match.range, in: text) else { continue }
            let name = String(text[r])
            guard !stopList.contains(name), !seen.contains(name) else { continue }
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
}

/// Persists codename sightings into the store, merging regex and LLM sources.
struct CodenameRegistry: Sendable {
    let store: Store

    func recordFromCommand(_ cmd: UserCommand) {
        for name in CodenameDetector.detect(in: cmd.text) {
            store.recordCodename(name: name, definition: "", nodeId: cmd.id, seenAt: cmd.timestamp)
        }
    }

    func recordFromSummary(_ summary: Summary, nodeId: String, seenAt: Date) {
        for def in summary.codenames {
            let name = def.name.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !name.isEmpty else { continue }
            store.recordCodename(name: name, definition: def.definition, nodeId: nodeId, seenAt: seenAt)
        }
    }
}
