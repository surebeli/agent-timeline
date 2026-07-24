import Foundation

/// UserDefaults-backed settings. Views bind via @AppStorage with the same keys;
/// non-UI components read through this wrapper and observe
/// UserDefaults.didChangeNotification to react live.
enum SettingsKey {
    static let engineMode = "engineMode"                // SummaryEngineKind rawValue
    static let cliModel = "cliModel"
    static let providerBaseURL = "providerBaseURL"
    static let providerAPIKey = "providerAPIKey"
    static let providerModel = "providerModel"
    static let hoverOpacity = "hoverOpacity"
    static let idleOpacity = "idleOpacity"
    static let alwaysOnTop = "alwaysOnTop"
    static let backfillDays = "backfillDays"
    static let agentClaudeEnabled = "agentClaudeEnabled"
    static let agentCodexEnabled = "agentCodexEnabled"
    static let agentKimiEnabled = "agentKimiEnabled"
    static let agentZcodeEnabled = "agentZcodeEnabled"
    static let zcodeSessionPath = "zcodeSessionPath"
}

struct AppSettings: Sendable {
    static func registerDefaults() {
        UserDefaults.standard.register(defaults: [
            SettingsKey.engineMode: SummaryEngineKind.cli.rawValue,
            SettingsKey.cliModel: "haiku",
            SettingsKey.providerBaseURL: "",
            SettingsKey.providerAPIKey: "",
            SettingsKey.providerModel: "",
            SettingsKey.hoverOpacity: DesignTokens.shared.opacity.hover,
            SettingsKey.idleOpacity: DesignTokens.shared.opacity.idle,
            SettingsKey.alwaysOnTop: true,
            SettingsKey.backfillDays: 7,
            SettingsKey.agentClaudeEnabled: true,
            SettingsKey.agentCodexEnabled: true,
            SettingsKey.agentKimiEnabled: true,
            SettingsKey.agentZcodeEnabled: false,
            SettingsKey.zcodeSessionPath: "",
        ])
    }

    static var engineMode: SummaryEngineKind {
        SummaryEngineKind(rawValue: UserDefaults.standard.string(forKey: SettingsKey.engineMode) ?? "") ?? .cli
    }
    static var cliModel: String { UserDefaults.standard.string(forKey: SettingsKey.cliModel) ?? "haiku" }
    static var providerBaseURL: String { UserDefaults.standard.string(forKey: SettingsKey.providerBaseURL) ?? "" }
    static var providerAPIKey: String { UserDefaults.standard.string(forKey: SettingsKey.providerAPIKey) ?? "" }
    static var providerModel: String { UserDefaults.standard.string(forKey: SettingsKey.providerModel) ?? "" }
    static var hoverOpacity: Double { UserDefaults.standard.double(forKey: SettingsKey.hoverOpacity) }
    static var idleOpacity: Double { UserDefaults.standard.double(forKey: SettingsKey.idleOpacity) }
    static var alwaysOnTop: Bool { UserDefaults.standard.bool(forKey: SettingsKey.alwaysOnTop) }
    static var backfillDays: Int { UserDefaults.standard.integer(forKey: SettingsKey.backfillDays) }
    static var zcodeSessionPath: String { UserDefaults.standard.string(forKey: SettingsKey.zcodeSessionPath) ?? "" }

    static func isAgentEnabled(_ agent: AgentKind) -> Bool {
        let key: String
        switch agent {
        case .claude: key = SettingsKey.agentClaudeEnabled
        case .codex: key = SettingsKey.agentCodexEnabled
        case .kimi: key = SettingsKey.agentKimiEnabled
        case .zcode: key = SettingsKey.agentZcodeEnabled
        }
        return UserDefaults.standard.bool(forKey: key)
    }

    /// Dedicated working directory for headless summarizer CLI runs, so their own
    /// session files never pollute the watched agent directories.
    static var summarizerScratchDir: String {
        supportDir + "/summarizer"
    }

    static var supportDir: String {
        NSSearchPathForDirectoriesInDomains(.applicationSupportDirectory, .userDomainMask, true)[0]
            + "/AgentTimeline"
    }
}
