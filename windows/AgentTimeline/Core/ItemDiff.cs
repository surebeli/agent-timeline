namespace AgentTimeline.Core;

/// <summary>
/// 列表增量套用用的公共前后缀长度。
///
/// **为什么需要它**：时间线原先每次重排都 <c>Items.Clear()</c> 再整表重加，集合事件数恒等于
/// 列表长度。以前这不要紧——分页要手点底部的「加载更多」，列表通常就停在首页两百条；
/// 现在改成滚到底自动翻页，随手就是两三千条，而**每来一个新节点都要重排一次**
/// （本工程是实时监听器，节点是持续进来的）。事件数从两百级涨到几千级，代价随列表长度
/// 线性长，且长的正是用户正在读的那一屏。
///
/// 时间线的两条日常路径都只动列表一端——分页往尾部追加、新节点插到头部——保住公共
/// 前后缀后集合事件就只剩 Add/Insert，视口里已实现出来的元素一个都不动。
///
/// 放在 <c>Core</c> 下、签名不带任何 UI 类型，是为了能在 <c>CoreSmokeTest</c> 里断言。
/// </summary>
public static class ItemDiff
{
    /// <summary>
    /// 两个列表的公共前缀长度与公共后缀长度。两者之和不超过较短列表的长度
    /// （否则中段会被算成负数、插入区间反转）。
    /// </summary>
    /// <param name="same">槽位是否可互换。引用相等或值相等都行，由调用方定。</param>
    public static (int Prefix, int Suffix) CommonAffixes<T>(
        IReadOnlyList<T> current, IReadOnlyList<T> next, Func<T, T, bool> same)
    {
        var min = Math.Min(current.Count, next.Count);

        var prefix = 0;
        while (prefix < min && same(current[prefix], next[prefix])) prefix++;

        var suffix = 0;
        while (suffix < min - prefix
               && same(current[current.Count - 1 - suffix], next[next.Count - 1 - suffix]))
        {
            suffix++;
        }

        return (prefix, suffix);
    }
}
