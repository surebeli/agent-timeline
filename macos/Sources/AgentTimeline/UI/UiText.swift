import Foundation

/// 「落库值 → 显示标签」的唯一映射点。与 Windows `UI/UiText.cs` 同构。
///
/// `NodeKind` / `CodenameStatus` 落库的是**中文 rawValue**（与 Windows 共用同一串，
/// 两库才能直接比对），过滤条件也按它下推到 SQL。所以语言切换**只换渲染**，
/// 不动库里一个字节，也不动任何比较逻辑——这是 `design/strings.json` meta 里写死的约束。
///
/// 映射按枚举顺序而不是字符串 switch：两张表长度对不上时启动即断言失败，
/// 好过某个语言少一档、界面上悄悄回显键名。
enum UiText {
    /// 过滤器「全部」哨兵。**不能用中文词当选项值**——它同时是选项集合成员和比较判据，
    /// 语言一切换会连比较逻辑一起漂。冒号在路径分量里非法，真实项目名不可能相撞。
    /// 与 Windows `TimelineViewModel.AllProjects` / `AllKinds` 逐字相同，
    /// 将来要比对两端过滤状态才对得上。
    static let allProjects = "::all-projects::"
    static let allKinds = "::all-kinds::"

    private static let kindKeys = [
        "kind.requirement", "kind.task", "kind.research",
        "kind.learning", "kind.decision", "kind.fix", "kind.other",
    ]

    private static let statusKeys = [
        "status.defined", "status.inProgress", "status.done",
        "status.changed", "status.mentioned",
    ]

    /// 落库标签 → 键名。每条时间线条目渲染都要查，故预先建表而不是每次线性扫。
    private static let kindKeyByLabel: [String: String] = {
        assert(kindKeys.count == NodeKind.allCases.count,
               "kind.* 键数 \(kindKeys.count) 与 NodeKind 档数 \(NodeKind.allCases.count) 不一致")
        return Dictionary(uniqueKeysWithValues:
            zip(NodeKind.allCases.map(\.rawValue), kindKeys))
    }()

    private static let statusKeyByLabel: [String: String] = {
        assert(statusKeys.count == CodenameStatus.allCases.count,
               "status.* 键数 \(statusKeys.count) 与 CodenameStatus 档数不一致")
        return Dictionary(uniqueKeysWithValues:
            zip(CodenameStatus.allCases.map(\.rawValue), statusKeys))
    }()

    /// 类型标签（落库中文 → 当前语言）。未知值原样回显——LLM 输出不可信。
    static func kind(_ storedLabel: String?) -> String {
        guard let storedLabel, !storedLabel.isEmpty else { return "" }
        guard let key = kindKeyByLabel[storedLabel] else { return storedLabel }
        return Strings.s(key)
    }

    /// 代号状态标签（落库中文 → 当前语言）。
    static func status(_ storedLabel: String?) -> String {
        guard let storedLabel, !storedLabel.isEmpty else { return "" }
        guard let key = statusKeyByLabel[storedLabel] else { return storedLabel }
        return Strings.s(key)
    }

    static func status(_ status: CodenameStatus) -> String { Self.status(status.rawValue) }

    /// 过滤器选项的显示文本。`compact` 是折叠态按钮上的短标签（面板窄，装不下
    /// 「全部项目」这种长串），菜单项里则用完整措辞。
    static func projectOption(_ option: String, compact: Bool) -> String {
        option == allProjects
            ? Strings.s(compact ? "header.allProjects" : "filter.allProjectsItem")
            : option
    }

    static func kindOption(_ option: String, compact: Bool) -> String {
        option == allKinds
            ? Strings.s(compact ? "header.allKinds" : "filter.allKindsItem")
            : kind(option)
    }
}
