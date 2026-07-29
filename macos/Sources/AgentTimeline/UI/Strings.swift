import Foundation

/// 用户可选的界面语言。`.system` = 跟随操作系统。
///
/// rawValue 存**字符串**不存序号：设置是人可读的，将来加语言时不希望旧值语义被序号
/// 挪位。与 Windows `AppLanguage` 同名同值。
enum AppLanguage: String, CaseIterable, Sendable {
    case system = "System"
    case zhHans = "ZhHans"
    case en = "En"
    case ja = "Ja"
    case ko = "Ko"
}

/// 界面文案（design/strings.json 的双端共享表，构建期由 build-app.sh 嵌成
/// `StringsData.swift`）。与 Windows `AppStrings` 同语义实现。
///
/// 为什么不用 .xcstrings：语言由**应用内设置**决定，不依赖系统资源解析；两端又有大量
/// 代码构建的 UI（chip 弹层、词典面板、菜单栏菜单），原生资源在那些地方同样要手写查表。
/// 共享 JSON 则能一份文件译四语 + CI 硬校验键集合，与 design-tokens.json 同一套范式。
///
/// **平台覆盖**：键名可带 `@mac` / `@win` 后缀，查找时先试 `键名@mac` 再回退 `键名`。
/// 只在概念本身分叉时才拆（如 hideToTray——macOS 是菜单栏应用，没有托盘），
/// 不要为措辞差异拆键，那正是这张表要消灭的漂移。
///
/// **占位符**用 `{0}`/`{1}` 序号式，与 Windows `Format` 逐字同语义；**不要用 Swift
/// 字符串插值**——同一份表在另一端就成了字面量。
final class Strings: @unchecked Sendable {
    private static let platformSuffix = "@mac"
    private static let fallback = "en"

    /// 语言切换后发出，供代码构建的 UI 重建（菜单栏菜单等不会自动刷新）。
    static let didChangeNotification = Notification.Name("AgentTimeline.stringsDidChange")

    private let table: [String: [String: String]]

    /// 当前生效的语言标签（`zh-Hans` / `en` / `ja` / `ko`），已解析过「跟随系统」。
    let language: String

    private(set) static var current = Strings(table: [:], language: fallback)

    private init(table: [String: [String: String]], language: String) {
        self.table = table
        self.language = language
    }

    /// 载入文案表并解析语言。
    ///
    /// 解析失败**不抛**——挂件不该因为一份文案表读不动就起不来，退化成「键名原样显示」
    /// 比白屏好排查（与 `DesignTokens.shared` 同一姿态）。
    static func load(_ preference: AppLanguage) {
        let language = resolve(preference)
        var table: [String: [String: String]] = [:]
        if let data = StringsData.json.data(using: .utf8),
           let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let strings = root["strings"] as? [String: [String: String]] {
            table = strings
        } else {
            NSLog("[Strings] strings.json 解析失败，退化为回显键名")
        }
        current = Strings(table: table, language: language)
        NotificationCenter.default.post(name: didChangeNotification, object: nil)
    }

    /// 「跟随系统」时按系统 UI 语言取最接近的一档。
    ///
    /// `zh-TW` / `zh-HK` 也归到 `zh-Hans`——目前只有简体一份，给繁体用户简体也好过
    /// 直接掉到英文。兜底 `en`。
    private static func resolve(_ preference: AppLanguage) -> String {
        switch preference {
        case .zhHans: return "zh-Hans"
        case .en: return "en"
        case .ja: return "ja"
        case .ko: return "ko"
        case .system:
            let code = Locale.preferredLanguages.first
                .flatMap { Locale(identifier: $0).language.languageCode?.identifier }
            switch code {
            case "zh": return "zh-Hans"
            case "ja": return "ja"
            case "ko": return "ko"
            default: return fallback
            }
        }
    }

    /// 取文案。查不到时**回显键名**而不是空串：空串会让界面看起来「少了个控件」，
    /// 键名则一眼看出是漏了哪个键——这类缺失只有跑起来才暴露，得让它自曝。
    func get(_ key: String) -> String {
        if let hit = pick(table[key + Self.platformSuffix]) { return hit }
        if let hit = pick(table[key]) { return hit }
        return key
    }

    private func pick(_ langs: [String: String]?) -> String? {
        guard let langs else { return nil }
        if let hit = langs[language], !hit.isEmpty { return hit }
        if let en = langs[Self.fallback], !en.isEmpty { return en }
        return nil
    }

    /// 序号占位符替换（`{0}`/`{1}`…）。与 Windows `AppStrings.Format` 逐字同语义。
    func format(_ key: String, _ args: CVarArg...) -> String {
        formatList(key, args)
    }

    /// 变参转发用（Swift 的可变参数不能直接透传）。
    func formatList(_ key: String, _ args: [CVarArg]) -> String {
        var text = get(key)
        for (index, value) in args.enumerated() {
            text = text.replacingOccurrences(
                of: "{\(index)}", with: "\(value)", options: .literal)
        }
        return text
    }

    /// 快捷取用：`Strings.s("tray.exit")`。
    static func s(_ key: String) -> String { current.get(key) }

    static func f(_ key: String, _ args: CVarArg...) -> String { current.formatList(key, args) }
}

/// 语言变更的 SwiftUI 重渲染钩子。
///
/// `Strings.s(...)` 是普通函数调用，SwiftUI 不会因为表换了就重算 body——需要一个
/// 可观察对象把 `didChangeNotification` 转成 `objectWillChange`。代码构建的 UI
/// （菜单栏菜单）不走 SwiftUI，另行监听同一个通知重建。
@MainActor
final class LanguageWatcher: ObservableObject {
    static let shared = LanguageWatcher()

    private init() {
        NotificationCenter.default.addObserver(
            forName: Strings.didChangeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.objectWillChange.send() }
        }
    }
}
