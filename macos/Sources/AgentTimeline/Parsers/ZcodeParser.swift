import Foundation

/// zcode 适配器（mac 端待实现，Roadmap M4）。格式规范已定稿见
/// docs/SESSION-FORMATS.md §4，Windows 端已按该规范实现并实机验证；
/// 根目录内建为 `~/.zcode/cli/agents`（不再作为可配项——路径是产品事实不是偏好）。
/// 填 `parse` 时照其他解析器的模式即可，协议不变。
struct ZcodeParser: AgentSessionParser {
    let agent = AgentKind.zcode

    /// 内建默认根（与 win 一致）；mac 端 parse 未实现前不挂 watcher。
    static let defaultRoot = "~/.zcode/cli/agents"

    func watchRoots() -> [URL] { [] }

    func makeContext(for url: URL) -> ParsedFileContext? {
        guard url.lastPathComponent == "transcript.jsonl",
              url.path.hasPrefix(ParserSupport.home(Self.defaultRoot).path) else { return nil }
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
