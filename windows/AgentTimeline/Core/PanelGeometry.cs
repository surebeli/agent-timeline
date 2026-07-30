namespace AgentTimeline.Core;

/// <summary>
/// 面板「折叠到只剩标题栏」的几何计算（对齐 mac <c>FloatingPanel.collapsedFrame</c>）。
///
/// **纯函数、只用基元类型、放在 Core 下**——这三条是为了能在 <c>CoreSmokeTest</c> 里断言：
/// 那个工程是 net7.0、不引 Windows App SDK、只编译 <c>Core/**</c>，所以签名里不能出现
/// <c>RectInt32</c>（交接任务书的伪代码用了它，编不过）。<c>RectInt32</c> 的转换留在
/// <c>MainWindow</c> 一侧。
///
/// ⚠ **坐标系与 mac 相反**。mac 是 Cocoa：Y 轴向上、原点在屏幕左下，"顶边不动"要写成
/// <c>origin.y += 旧高 - 新高</c>。Windows 是 Win32 屏幕坐标：**Y 轴向下、原点在左上**，
/// 顶边就是 <c>Y</c> 本身——"顶边不动"等于 <b>Y 保持不变、只改 Height</b>。
/// 照抄 mac 那行加法，折叠后窗口会往屏幕下方跳。
/// </summary>
public static class PanelGeometry
{
    // ── 折叠高度：**按本端头部布局推导，不抄 mac 的 41pt**
    //
    // mac 的 41 = 它自己的 padding(top:4) + frame(height:28) + padding(bottom:8) + 分隔线 1。
    // Windows 头部是 MainWindow.xaml 的 HeaderBar：Padding="12,10,12,6"，内容是
    // VerticalAlignment=Center 的一行控件，最高的是 HeaderIconButtonStyle 的图标按钮
    // （Padding=4 + FontIcon FontSize=12 → 内容盒约 20，实测行高 24）。
    //
    // 这些常量放这里是为了**让冒烟断言守住推导式**：头部布局改了、这里没跟着改，
    // 运行时会与实测高度对不上并写进 app.log（见 MainWindow.ApplyCollapsed）。
    // 真正用于折叠的是**运行时实测的 HeaderBar.ActualHeight**——常量只是推导基准与兜底，
    // 免得留一个会过期的写死数字。

    /// <summary>HeaderBar 的上内边距（MainWindow.xaml <c>Padding="12,10,12,6"</c>）。</summary>
    public const int HeaderPaddingTop = 10;

    /// <summary>HeaderBar 的下内边距（同上）。</summary>
    public const int HeaderPaddingBottom = 6;

    /// <summary>
    /// 头部行内**最高控件**的实测行高。
    ///
    /// ⚠ 不是图标按钮（Padding 4×2 + 12px 字形 ≈ 24）——行里最高的是带文字的过滤按钮
    /// （<c>Padding="5,3,5,4"</c> + FontSizeBody 文本）。第一版按图标按钮推成 24，
    /// 运行时自检立刻报了「推导 40dip vs 实测 43px」，按实测订正为 27。
    /// 这就是那道自检存在的意义：写死的推导值一定会过期，得让它自己喊出来。
    /// </summary>
    public const int HeaderRowHeight = 27;

    /// <summary>推导出的折叠高度（dip）。运行时以实测为准，这里是基准与兜底。</summary>
    public const int CollapsedHeightDip = HeaderPaddingTop + HeaderRowHeight + HeaderPaddingBottom;

    /// <summary>
    /// 展开态的最小高度（dip）。折叠是**显式操作**，不能让人拖拽把窗口缩到折叠尺寸——
    /// 那样"已折叠"标志与实际高度就脱钩了（mac 同名常量同一理由）。
    /// </summary>
    public const int ExpandedMinHeightDip = 320;

    /// <summary>
    /// 折叠 / 展开后的窗口高度（物理像素）。<paramref name="collapsedHeight"/> 与
    /// <paramref name="expandedHeight"/> 都是物理像素，由调用方按 DPI 换算好再传进来。
    ///
    /// 只返回高度：X / Y / Width 一律不动——顶边即 Y，Win32 坐标系下"顶边不动"就是不碰 Y。
    /// </summary>
    public static int TargetHeight(bool collapsed, int collapsedHeight, int expandedHeight, int expandedMinHeight)
    {
        if (collapsed) return collapsedHeight;
        // 存量/异常的展开高度不能把窗口还原成一条缝
        return Math.Max(expandedMinHeight, expandedHeight);
    }

    /// <summary>
    /// 设置里读出的展开高度的取值优先级（对应交接 review 的 W-3）。
    ///
    /// 老用户升级上来时 <c>PanelExpandedHeight</c> 不存在、只有 <c>WindowHeight</c>：
    ///   1. <c>PanelExpandedHeight</c> 有效（&gt; 折叠高度）→ 用它；
    ///   2. 否则退回 <c>WindowHeight</c>，但**仅当它也大于折叠高度**——否则说明用户上次
    ///      退出时正处折叠态，那个字段存的就是折叠尺寸，拿它当展开高度会展开成一条缝；
    ///   3. 都不行 → 用 tokens 的默认高度。
    /// 最后再抬到 <paramref name="expandedMinHeight"/> 之上。
    /// </summary>
    public static int ResolveExpandedHeight(
        int savedExpandedHeight, int savedWindowHeight, int defaultHeight,
        int collapsedHeight, int expandedMinHeight)
    {
        var picked =
            savedExpandedHeight > collapsedHeight ? savedExpandedHeight :
            savedWindowHeight > collapsedHeight ? savedWindowHeight :
            defaultHeight;
        return Math.Max(expandedMinHeight, picked);
    }

    /// <summary>
    /// 从展开态折叠时，当前高度是否**允许**被记成"折叠前高度"。
    ///
    /// mac 实机踩过的坑：启动时若上次是折叠的，会走"折叠"这个动作本身，而那个动作
    /// 顺手把当前高度记成折叠前高度——可当前高度已经是折叠尺寸了，于是把用户真正的
    /// 高度冲掉，展开后只剩一条缝。判据就是这一条。
    /// </summary>
    public static bool CanRecordExpandedHeight(int currentHeight, int collapsedHeight) =>
        currentHeight > collapsedHeight;

    // ── 滚到底自动取下一页
    //
    // 早先靠底部一个「加载更多」按钮翻页。它不是多余的**功能**（`PageSize=200`，真实库
    // 5000+ 条，不翻页就只能看到最新 200 条），但作为**入口形式**是多余的：`HasMore`
    // 在大库上恒为真，按钮钉在滚动区之外、在 340~580dip 高的挂件里常驻占掉一行。
    // 改成滚到底自动加载后按钮撤掉，功能不丢、版面省一行。

    /// <summary>触发预取的距底阈值（dip）。留一屏的四分之一左右，滚到底前就已经接上。</summary>
    public const double LoadMoreThresholdDip = 120;

    /// <summary>
    /// 当前滚动位置是否该取下一页。纯函数，几何量由调用方从 ScrollViewer 读。
    ///
    /// 三道闸都必要：
    /// · <paramref name="hasMore"/>——没有下一页就别问库；
    /// · <paramref name="loading"/>——`ViewChanged` 在一次滚动里会连发多拍，而取完一页会
    ///   追加内容、`ExtentHeight` 变大又触发 `ViewChanged`；不挡住会连着把整库拉完；
    /// · <paramref name="extentHeight"/> ≤ <paramref name="viewportHeight"/>——内容还没
    ///   撑满一屏时距底恒为 0，会在启动瞬间就无条件预取。
    /// </summary>
    public static bool ShouldLoadMore(
        double verticalOffset, double viewportHeight, double extentHeight,
        bool hasMore, bool loading, double threshold)
    {
        if (!hasMore || loading) return false;
        if (viewportHeight <= 0 || extentHeight <= viewportHeight) return false;
        return extentHeight - (verticalOffset + viewportHeight) <= threshold;
    }
}
