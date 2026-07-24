import CryptoKit
import Foundation

enum AgentKind: String, Codable, CaseIterable, Identifiable, Sendable {
    case claude, codex, kimi, zcode

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .claude: return "Claude"
        case .codex: return "Codex"
        case .kimi: return "Kimi"
        case .zcode: return "zcode"
        }
    }
}

struct UserCommand: Sendable, Equatable {
    let id: String
    let agent: AgentKind
    let project: String
    let cwd: String?
    let sessionId: String
    let timestamp: Date
    let text: String
    let sourceFile: String

    init(agent: AgentKind, project: String, cwd: String?, sessionId: String,
         timestamp: Date, text: String, sourceFile: String) {
        self.agent = agent
        self.project = project
        self.cwd = cwd
        self.sessionId = sessionId
        self.timestamp = timestamp
        self.text = text
        self.sourceFile = sourceFile
        self.id = Self.stableId(agent: agent, sessionId: sessionId, timestamp: timestamp, text: text)
    }

    /// Deterministic so re-parsing a file after an offset reset never duplicates nodes.
    static func stableId(agent: AgentKind, sessionId: String, timestamp: Date, text: String) -> String {
        let seed = "\(agent.rawValue)|\(sessionId)|\(Int(timestamp.timeIntervalSince1970 * 1000))|\(text)"
        let digest = SHA256.hash(data: Data(seed.utf8))
        return digest.prefix(12).map { String(format: "%02x", $0) }.joined()
    }
}

struct CodenameDef: Codable, Sendable, Equatable {
    let name: String
    let definition: String
}

struct Summary: Codable, Sendable, Equatable {
    var title: String
    var keyPoints: [String]
    var codenames: [CodenameDef]
    var resultLine: String?
    /// "rule" | "cli" | "provider"
    var engine: String

    var isLLM: Bool { engine != SummaryEngineKind.rule.rawValue }
}

enum SummaryEngineKind: String, CaseIterable, Sendable {
    case cli, provider, rule
}

struct TimelineNode: Identifiable, Sendable, Equatable {
    var command: UserCommand
    var summary: Summary?
    var id: String { command.id }
}

struct CodenameEntry: Identifiable, Sendable, Equatable {
    let name: String
    var definition: String
    var definitionNodeId: String
    var firstSeen: Date
    var occurrences: Int
    var id: String { name }
}

enum SessionEvent: Sendable {
    case userCommand(UserCommand)
    /// Latest assistant/agent output for a session; used to fill the previous node's resultLine.
    case assistantText(agent: AgentKind, sessionId: String, timestamp: Date, text: String)
}
