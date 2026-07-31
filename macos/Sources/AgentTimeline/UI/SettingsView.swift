import SwiftUI

struct SettingsView: View {
    /// 语言切换后重算 body——Strings.s(...) 是普通函数调用，SwiftUI 不会自己知道表换了。
    @ObservedObject private var languageWatcher = LanguageWatcher.shared

    @AppStorage(SettingsKey.engineMode) private var engineMode = SummaryEngineKind.cli.rawValue
    @AppStorage(SettingsKey.cliModel) private var cliModel = "haiku"
    @AppStorage(SettingsKey.providerBaseURL) private var providerBaseURL = ""
    @AppStorage(SettingsKey.providerAPIKey) private var providerAPIKey = ""
    @AppStorage(SettingsKey.providerModel) private var providerModel = ""
    @AppStorage(SettingsKey.hoverOpacity) private var hoverOpacity = 0.95
    @AppStorage(SettingsKey.idleOpacity) private var idleOpacity = 0.25
    @AppStorage(SettingsKey.alwaysOnTop) private var alwaysOnTop = true
    @AppStorage(SettingsKey.launchAtLogin) private var launchAtLogin = true
    @AppStorage(SettingsKey.backfillDays) private var backfillDays = 7
    @AppStorage(SettingsKey.agentClaudeEnabled) private var claudeEnabled = true
    @AppStorage(SettingsKey.agentCodexEnabled) private var codexEnabled = true
    @AppStorage(SettingsKey.agentGrokEnabled) private var grokEnabled = true
    @AppStorage(SettingsKey.agentKimiEnabled) private var kimiEnabled = true
    @AppStorage(SettingsKey.agentZcodeEnabled) private var zcodeEnabled = false

    @AppStorage(SettingsKey.language) private var language = AppLanguage.system.rawValue

    let onApply: () -> Void

    /// 语言下拉用**本族语自称**，只有「跟随系统」跟着翻译——界面正显示着看不懂的语言时，
    /// 用户要能认出自己那一档。这四条按约定**不进文案表**。
    private static let languageLabels: [(AppLanguage, String)] = [
        (.zhHans, "中文"), (.en, "English"), (.ja, "日本語"), (.ko, "한국어"),
    ]

    var body: some View {
        Form {
            Section(Strings.s("settings.section.engine")) {
                Picker(Strings.s("settings.section.engine"), selection: $engineMode) {
                    Text(Strings.s("settings.engine.cli")).tag(SummaryEngineKind.cli.rawValue)
                    Text(Strings.s("settings.engine.provider")).tag(SummaryEngineKind.provider.rawValue)
                    Text(Strings.s("settings.engine.rule")).tag(SummaryEngineKind.rule.rawValue)
                }
                .pickerStyle(.radioGroup)

                if engineMode == SummaryEngineKind.cli.rawValue {
                    TextField(Strings.s("settings.model"), text: $cliModel, prompt: Text("haiku"))
                }
                if engineMode == SummaryEngineKind.provider.rawValue {
                    TextField("Base URL", text: $providerBaseURL, prompt: Text("https://api.example.com/v1"))
                    SecureField("API Key", text: $providerAPIKey)
                    TextField("Model", text: $providerModel, prompt: Text("model-name"))
                }
            }

            Section(Strings.s("settings.section.appearance")) {
                // 语言即时生效：mac 设置是 @AppStorage 直写模型，没有「未保存」状态，
                // 故无需 Windows 那套关窗回滚——那是给缓冲式保存窗口用的。
                Picker(Strings.s("settings.language"), selection: $language) {
                    Text(Strings.s("settings.language.system")).tag(AppLanguage.system.rawValue)
                    ForEach(Self.languageLabels, id: \.0) { lang, label in
                        Text(label).tag(lang.rawValue)
                    }
                }
                .onChange(of: language) { _, newValue in
                    Strings.load(AppLanguage(rawValue: newValue) ?? .system)
                }
                Text(Strings.s("settings.language.note"))
                    .font(.caption)
                    .foregroundStyle(.secondary)

                Toggle(Strings.s("settings.alwaysOnTop"), isOn: $alwaysOnTop)
                // 侧效交给 LoginItem.sync：register()/unregister() 要立即调用，不能等
                // 「应用」按钮——这颗开关本身就是即时生效的（跟别的设置一样是 @AppStorage
                // 直写模型），拖到点应用才同步反而会让开关状态与系统实际状态短暂对不上。
                Toggle(Strings.s("settings.launchAtLogin"), isOn: $launchAtLogin)
                    .onChange(of: launchAtLogin) { _, newValue in
                        LoginItem.sync(desired: newValue)
                    }
                LabeledContent("\(Strings.s("settings.hoverOpacity")) \(hoverOpacity, format: .number.precision(.fractionLength(2)))") {
                    Slider(value: $hoverOpacity, in: 0.5...1.0)
                }
                LabeledContent("\(Strings.s("settings.idleOpacity")) \(idleOpacity, format: .number.precision(.fractionLength(2)))") {
                    Slider(value: $idleOpacity, in: 0.05...1.0)   // 1.0 = 不淡出（win 已支持）
                }
            }

            // Session 路径是产品事实（各 agent 自己定的），不是可配项——
            // 全部内建自动发现，故这里只留开关不留路径输入。
            Section(Strings.s("settings.sessionSources")) {
                // 标签一律取 AgentKind.settingsLabel，保证与 Windows
                // SettingsWindow.xaml 的 CheckBox Content 不会各自漂移；
                // 顺序 = AgentKind 声明顺序（Claude Code / Codex / Grok Build /
                // Kimi Code / ZCode）。
                Toggle(AgentKind.claude.settingsLabel, isOn: $claudeEnabled)
                Toggle(AgentKind.codex.settingsLabel, isOn: $codexEnabled)
                Toggle(AgentKind.grok.settingsLabel, isOn: $grokEnabled)
                Toggle(AgentKind.kimi.settingsLabel, isOn: $kimiEnabled)
                Toggle(AgentKind.zcode.settingsLabel, isOn: $zcodeEnabled)
                Stepper(Strings.f("settings.backfillDays", backfillDays), value: $backfillDays, in: 0...90)
            }

            Section {
                Button(Strings.s("settings.apply")) { onApply() }
            }
        }
        .formStyle(.grouped)
        .frame(width: 420)
        .fixedSize(horizontal: false, vertical: true)
    }
}
