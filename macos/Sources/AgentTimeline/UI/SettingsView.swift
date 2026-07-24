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
    @AppStorage(SettingsKey.agentKimiEnabled) private var kimiEnabled = true
    @AppStorage(SettingsKey.agentZcodeEnabled) private var zcodeEnabled = false
    @AppStorage(SettingsKey.zcodeSessionPath) private var zcodePath = ""

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
                    TextField("CLI 模型（claude --model）", text: $cliModel)
                        .help("传给 claude -p 的 --model，例如 haiku；留空用 CLI 默认")
                }
                if engineMode == SummaryEngineKind.provider.rawValue {
                    TextField("Base URL", text: $providerBaseURL, prompt: Text("https://api.example.com/v1"))
                    SecureField("API Key", text: $providerAPIKey)
                    TextField("Model", text: $providerModel, prompt: Text("model-name"))
                }
            }

            Section("窗口") {
                Toggle("窗口置顶（always on top）", isOn: $alwaysOnTop)
                LabeledContent("hover 不透明度 \(hoverOpacity, format: .number.precision(.fractionLength(2)))") {
                    Slider(value: $hoverOpacity, in: 0.5...1.0)
                }
                LabeledContent("失焦不透明度 \(idleOpacity, format: .number.precision(.fractionLength(2)))") {
                    Slider(value: $idleOpacity, in: 0.05...0.8)
                }
            }

            Section("Session 来源") {
                Toggle("Claude Code（~/.claude/projects）", isOn: $claudeEnabled)
                Toggle("Codex（~/.codex/sessions）", isOn: $codexEnabled)
                Toggle("Kimi（~/.kimi/sessions）", isOn: $kimiEnabled)
                Toggle("zcode（需配置路径）", isOn: $zcodeEnabled)
                if zcodeEnabled {
                    TextField("zcode session 根目录", text: $zcodePath, prompt: Text("~/.zcode/sessions"))
                    Text("zcode 解析器为预留实现：拿到样例 session 文件后在 ZcodeParser 中补齐格式即可。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Stepper("启动回填最近 \(backfillDays) 天", value: $backfillDays, in: 1...60)
            }

            Section {
                Button("应用（重启监听与摘要队列）") { onApply() }
            }
        }
        .formStyle(.grouped)
        .frame(width: 440)
        .fixedSize(horizontal: false, vertical: true)
    }
}
