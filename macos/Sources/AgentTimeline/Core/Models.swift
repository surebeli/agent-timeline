import CryptoKit
import Foundation

/// 声明顺序 = 设置页与下拉里的展示顺序（`allCases` 直接驱动）：
/// Claude Code / Codex / Grok Build / Kimi Code / ZCode。
/// `rawValue` 是落库与 design-token 查找的稳定串，**不随展示名变动**。
enum AgentKind: String, Codable, CaseIterable, Identifiable, Sendable {
    case claude, codex, grok, kimi, zcode

    var id: String { rawValue }

    /// 时间线行内短名。设置页用完整产品名（`settingsLabel`），两级命名是既有约定。
    /// ⚠ 摘要 prompt 也用它，改动必须与 Windows `AgentKindExtensions.DisplayName()`
    /// 逐字一致，否则两端 prompt 不再同源。
    var displayName: String {
        switch self {
        case .claude: return "Claude"
        case .codex: return "Codex"
        case .grok: return "Grok"
        case .kimi: return "Kimi"
        case .zcode: return "ZCode"
        }
    }

    /// 设置页里的完整产品名（与 Windows SettingsWindow.xaml 的 CheckBox Content 一致）。
    var settingsLabel: String {
        switch self {
        case .claude: return "Claude Code"
        case .codex: return "Codex"
        case .grok: return "Grok Build"
        case .kimi: return "Kimi Code"
        case .zcode: return "ZCode"
        }
    }

    /// Two-letter source badge, shared visual language with the Windows side
    /// (AgentKind.Monogram there — keep the mappings in lockstep).
    var monogram: String {
        switch self {
        case .claude: return "CL"
        case .codex: return "CO"
        case .grok: return "GR"
        case .kimi: return "KI"
        case .zcode: return "ZC"
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
    /// CodenameStatus rawValue when the LLM saw a lifecycle signal; optional so
    /// cached pre-lifecycle rows still decode.
    var status: String?

    init(name: String, definition: String, status: String? = nil) {
        self.name = name
        self.definition = definition
        self.status = status
    }
}

/// Node phase classification — the "anchor" facet of the timeline.
enum NodeKind: String, Codable, CaseIterable, Sendable {
    case requirement = "需求"
    case task = "任务"
    case research = "调研"
    case learning = "学习"
    case decision = "决策"
    case fix = "修复"
    case other = "其他"
}

struct Summary: Codable, Sendable, Equatable {
    var title: String
    var keyPoints: [String]
    var codenames: [CodenameDef]
    var resultLine: String?
    /// "rule" | "cli" | "provider"
    var engine: String
    /// NodeKind rawValue; optional so cached pre-lifecycle rows still decode.
    var kind: String?

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
    /// CodenameStatus rawValue; empty until a lifecycle signal is seen.
    var status: String = ""
    var statusNodeId: String = ""
    var updated: Date?
    /// Excerpt around the most recent mention ("…N2完成，开始 N3…").
    var lastContext: String = ""
    var id: String { name }

    var statusValue: CodenameStatus? { CodenameStatus(rawValue: status) }
}

enum SessionEvent: Sendable {
    case userCommand(UserCommand)
    /// Latest assistant/agent output for a session; used to fill the previous node's resultLine.
    case assistantText(agent: AgentKind, sessionId: String, timestamp: Date, text: String)
}
