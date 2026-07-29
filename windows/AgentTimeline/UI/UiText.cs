using AgentTimeline.Core;

namespace AgentTimeline.UI;

/// <summary>
/// 「落库值 → 显示标签」的唯一映射点。
///
/// <c>NodeKind</c> / <c>CodenameStatus</c> 落库的是**中文 rawValue**（与 mac 端共用同一
/// 串，两库才能直接比对），过滤条件也按它下推到 SQL。所以语言切换**只换渲染**，
/// 不动库里一个字节，也不动任何比较逻辑——这是 design/strings.json meta 里写死的约束。
///
/// 映射按枚举序号而不是字符串 switch：两张表长度对不上时构造期就炸，
/// 好过某个语言少一档、界面上悄悄回显键名。
/// </summary>
public static class UiText
{
    private static readonly string[] KindKeys =
    {
        "kind.requirement", "kind.task", "kind.research",
        "kind.learning", "kind.decision", "kind.fix", "kind.other",
    };

    private static readonly string[] StatusKeys =
    {
        "status.defined", "status.inProgress", "status.done",
        "status.changed", "status.mentioned",
    };

    static UiText()
    {
        if (KindKeys.Length != NodeKinds.AllLabels.Count)
        {
            throw new InvalidOperationException(
                $"kind.* 键数 {KindKeys.Length} 与 NodeKind 档数 {NodeKinds.AllLabels.Count} 不一致");
        }
        if (StatusKeys.Length != Enum.GetValues<CodenameStatus>().Length)
        {
            throw new InvalidOperationException(
                $"status.* 键数 {StatusKeys.Length} 与 CodenameStatus 档数不一致");
        }
    }

    /// <summary>落库标签 → 键名。每条时间线条目渲染都要查，故预先建表而不是每次线性扫。</summary>
    private static readonly Dictionary<string, string> KindKeyByLabel =
        NodeKinds.AllLabels
            .Select((label, i) => (label, key: KindKeys[i]))
            .ToDictionary(x => x.label, x => x.key, StringComparer.Ordinal);

    /// <summary>类型标签（落库中文 → 当前语言）。未知值原样回显——LLM 输出不可信。</summary>
    public static string Kind(string? storedLabel)
    {
        if (string.IsNullOrEmpty(storedLabel)) return "";
        return KindKeyByLabel.TryGetValue(storedLabel, out var key) ? AppStrings.S(key) : storedLabel;
    }

    /// <summary>代号状态标签（落库中文 → 当前语言）。</summary>
    public static string Status(string? storedLabel)
    {
        if (string.IsNullOrEmpty(storedLabel)) return "";
        var status = CodenameStatuses.FromLabel(storedLabel);
        return status is { } s ? AppStrings.S(StatusKeys[(int)s]) : storedLabel;
    }

    /// <summary>
    /// 过滤器选项的显示文本。<paramref name="compact"/> 是折叠态按钮上的短标签
    /// （面板仅 340px 宽，装不下"全部项目"这种长串——见 TimelineViewModel 的注释），
    /// 菜单项里则用完整措辞。
    /// </summary>
    public static string ProjectOption(string option, bool compact) =>
        option == TimelineViewModel.AllProjects
            ? AppStrings.S(compact ? "header.allProjects" : "filter.allProjectsItem")
            : option;

    public static string KindOption(string option, bool compact) =>
        option == TimelineViewModel.AllKinds
            ? AppStrings.S(compact ? "header.allKinds" : "filter.allKindsItem")
            : Kind(option);
}
