import SwiftUI

struct SettingsView: View {
    @AppStorage(SettingsKey.engineMode) private var engineMode = SummaryEngineKind.cli.rawValue
    @AppStorage(SettingsKey.cliModel) private var cliModel = "haiku"
    @AppStorage(SettingsKey.providerBaseURL) private var providerBaseURL = ""
    @AppStorage(SettingsKey.providerAPIKey) private var providerAPIKey = ""
    @AppStorage(SettingsKey.providerModel) private var providerModel = ""
    @AppStorage(SettingsKey.hoverOpacity) private var hoverOpacity = 0.95
    @AppStorage(SettingsKey.idleOpacity) private var idleOpacity = 0.25
    @AppStorage(SettingsKey.alwaysOnTop) private var alwaysOnTop = true
    @AppStorage(SettingsKey.backfillDays) private var backfillDays = 7
    @AppStorage(SettingsKey.agentClaudeEnabled) private var claudeEnabled = true
    @AppStorage(SettingsKey.agentCodexEnabled) private var codexEnabled = true
    @AppStorage(SettingsKey.agentGrokEnabled) private var grokEnabled = true
    @AppStorage(SettingsKey.agentKimiEnabled) private var kimiEnabled = true
    @AppStorage(SettingsKey.agentZcodeEnabled) private var zcodeEnabled = false

    let onApply: () -> Void

    var body: some View {
        Form {
            Section("摘要引擎") {
                Picker("引擎", selection: $engineMode) {
                    Text("复用本机 CLI（推荐，零配置）").tag(SummaryEngineKind.cli.rawValue)
                    Text("自定义 Provider（OpenAI 兼容）").tag(SummaryEngineKind.provider.rawValue)
                    Text("纯规则（不调用模型）").tag(SummaryEngineKind.rule.rawValue)
                }
                .pickerStyle(.radioGroup)

                if engineMode == SummaryEngineKind.cli.rawValue {
                    TextField("模型", text: $cliModel, prompt: Text("haiku"))
                }
                if engineMode == SummaryEngineKind.provider.rawValue {
                    TextField("Base URL", text: $providerBaseURL, prompt: Text("https://api.example.com/v1"))
                    SecureField("API Key", text: $providerAPIKey)
                    TextField("Model", text: $providerModel, prompt: Text("model-name"))
                }
            }

            Section("窗口") {
                Toggle("窗口置顶", isOn: $alwaysOnTop)
                LabeledContent("hover 不透明度 \(hoverOpacity, format: .number.precision(.fractionLength(2)))") {
                    Slider(value: $hoverOpacity, in: 0.5...1.0)
                }
                LabeledContent("失焦不透明度 \(idleOpacity, format: .number.precision(.fractionLength(2)))") {
                    Slider(value: $idleOpacity, in: 0.05...1.0)   // 1.0 = 不淡出（win 已支持）
                }
            }

            // Session 路径是产品事实（各 agent 自己定的），不是可配项——
            // 全部内建自动发现，故这里只留开关不留路径输入。
            Section("Session 来源") {
                // 标签一律取 AgentKind.settingsLabel，保证与 Windows
                // SettingsWindow.xaml 的 CheckBox Content 不会各自漂移；
                // 顺序 = AgentKind 声明顺序（Claude Code / Codex / Grok Build /
                // Kimi Code / ZCode）。
                Toggle(AgentKind.claude.settingsLabel, isOn: $claudeEnabled)
                Toggle(AgentKind.codex.settingsLabel, isOn: $codexEnabled)
                Toggle(AgentKind.grok.settingsLabel, isOn: $grokEnabled)
                Toggle(AgentKind.kimi.settingsLabel, isOn: $kimiEnabled)
                Toggle(AgentKind.zcode.settingsLabel, isOn: $zcodeEnabled)
                Stepper("启动回填最近 \(backfillDays) 天", value: $backfillDays, in: 0...90)
            }

            Section {
                Button("应用") { onApply() }
            }
        }
        .formStyle(.grouped)
        .frame(width: 420)
        .fixedSize(horizontal: false, vertical: true)
    }
}
