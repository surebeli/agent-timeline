namespace AgentTimeline.Core;

/// <summary>
/// 代号词典关键字搜索的判据。**两端逐字一致**（mac
/// `TimelineViewModel.filterCodenames(_:matching:)`，见 windows/SYNC-KICKOFF-PROMPT.md
/// 2026-08-01 轮第 2 节）：
///
/// <code>
/// match(entry, query) =
///     name.contains(q) || definition.contains(q) || lastContext.contains(q)   // 全部小写比较
/// </code>
///
/// 三条语义都是有来由的，别"顺手优化"掉：
/// ① **子串而非前缀**——复合代号（`REQ-AUTH-3`、`T-PLUGIN-00`）用户常只记得中间一段；
/// ② **匹配范围含定义与最近提及**——用户可能只记得内容（"登录相关的那个"）而不记得
///    代号叫 N1 还是 N2；mac 实机验证里搜 "N1" 命中的 3 条有 2 条是靠 lastContext 命中的；
/// ③ **空白查询 = 没有搜索词**，返回全部而不是返回空。
///
/// 大小写用 <see cref="StringComparison.OrdinalIgnoreCase"/>：与区域设置无关，
/// 避免土耳其语 I 那类"换个系统语言搜索结果就变"的坑（mac 侧 `lowercased()` 同样
/// 不受用户界面语言影响）。
/// </summary>
public static class CodenameSearch
{
    /// <summary>按 <paramref name="query"/> 过滤；空/纯空白查询原样返回全部。</summary>
    public static List<CodenameEntry> Filter(IReadOnlyList<CodenameEntry> entries, string? query)
    {
        var needle = (query ?? "").Trim();
        if (needle.Length == 0) return entries.ToList();

        var hits = new List<CodenameEntry>();
        foreach (var entry in entries)
        {
            if (Matches(entry, needle)) hits.Add(entry);
        }
        return hits;
    }

    /// <summary>单条判定（三个字段任一命中即算命中）。</summary>
    public static bool Matches(CodenameEntry entry, string needle) =>
        ContainsFold(entry.Name, needle)
        || ContainsFold(entry.Definition, needle)
        || ContainsFold(entry.LastContext, needle);

    private static bool ContainsFold(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack)
        && haystack!.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
