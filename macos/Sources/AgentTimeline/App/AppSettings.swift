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
    static let launchAtLogin = "launchAtLogin"
    static let backfillDays = "backfillDays"
    static let agentClaudeEnabled = "agentClaudeEnabled"
    static let agentCodexEnabled = "agentCodexEnabled"
    static let agentGrokEnabled = "agentGrokEnabled"
    static let agentKimiEnabled = "agentKimiEnabled"
    static let agentZcodeEnabled = "agentZcodeEnabled"
    static let language = "language"                    // AppLanguage rawValue
    static let panelCollapsed = "panelCollapsed"        // 折叠到只剩 caption
    static let panelExpandedHeight = "panelExpandedHeight"  // 折叠前的高度，供还原
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
            SettingsKey.launchAtLogin: true,
            SettingsKey.backfillDays: 7,
            SettingsKey.agentClaudeEnabled: true,
            SettingsKey.agentCodexEnabled: true,
            SettingsKey.agentGrokEnabled: true,
            SettingsKey.agentKimiEnabled: true,
            SettingsKey.agentZcodeEnabled: true,
            SettingsKey.language: AppLanguage.system.rawValue,
            SettingsKey.panelCollapsed: false,
            SettingsKey.panelExpandedHeight: DesignTokens.shared.panel.defaultHeight,
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
    static var launchAtLogin: Bool { UserDefaults.standard.bool(forKey: SettingsKey.launchAtLogin) }
    static var backfillDays: Int { UserDefaults.standard.integer(forKey: SettingsKey.backfillDays) }

    /// 界面语言："System"（跟随系统，默认）/ "ZhHans" / "En" / "Ja" / "Ko"。
    ///
    /// 存字符串不存序号：设置是人可读的，加语言时不希望旧值语义被序号挪位。
    /// 切换**即时生效**；已入库的历史摘要保持原语言不重跑，但 kind / 代号状态 / 日期
    /// 这些**渲染标签**跟随（它们落库的是枚举值，不是文案）。与 Windows 同名同值。
    static var language: AppLanguage {
        AppLanguage(rawValue: UserDefaults.standard.string(forKey: SettingsKey.language) ?? "")
            ?? .system
    }

    /// 面板是否折叠到只剩 caption。
    static var panelCollapsed: Bool { UserDefaults.standard.bool(forKey: SettingsKey.panelCollapsed) }

    /// 折叠前的高度。折叠后 `panelFrame` 存的是折叠尺寸，不另存这一项就还原不回去。
    static var panelExpandedHeight: Double {
        let saved = UserDefaults.standard.double(forKey: SettingsKey.panelExpandedHeight)
        return saved > FloatingPanel.collapsedHeight ? saved : DesignTokens.shared.panel.defaultHeight
    }

    static func isAgentEnabled(_ agent: AgentKind) -> Bool {
        let key: String
        switch agent {
        case .claude: key = SettingsKey.agentClaudeEnabled
        case .codex: key = SettingsKey.agentCodexEnabled
        case .grok: key = SettingsKey.agentGrokEnabled
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
