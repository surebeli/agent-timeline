import Foundation

/// Placeholder adapter for zcode. The CLI is not installed on this machine and
/// no session sample exists yet (docs/SESSION-FORMATS.md §4). The session root
/// is user-configurable; once a sample is available, fill in `parse` following
/// the same pattern as the other parsers — the protocol will not change.
struct ZcodeParser: AgentSessionParser {
    let agent = AgentKind.zcode

    func watchRoots() -> [URL] {
        let path = AppSettings.zcodeSessionPath
        guard !path.isEmpty else { return [] }
        return [ParserSupport.home(path)]
    }

    func makeContext(for url: URL) -> ParsedFileContext? {
        let path = AppSettings.zcodeSessionPath
        guard !path.isEmpty,
              url.pathExtension == "jsonl",
              url.path.hasPrefix(ParserSupport.home(path).path) else { return nil }
        return ParsedFileContext(
            url: url, agent: .zcode,
            sessionId: url.deletingPathExtension().lastPathComponent,
            project: "zcode", cwd: nil)
    }

    func parse(line: String, context: inout ParsedFileContext) -> [SessionEvent] {
        // Format unknown — intentionally inert until a sample session lands.
        []
    }
}
