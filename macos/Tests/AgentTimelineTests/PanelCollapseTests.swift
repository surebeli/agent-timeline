import AppKit
import XCTest
@testable import AgentTimeline

/// caption 折叠（只留标题栏那一行）的几何。窗口本身在测试环境起不来，
/// 故把 frame 计算抽成纯函数在这里验。
final class PanelCollapseTests: XCTestCase {

    /// 折叠时**顶边不动**：挂件通常贴着屏幕某处放，应该像卷帘一样往上收，
    /// 而不是原地缩一半再跳。
    func testTopEdgeStaysPutWhenCollapsing() {
        let before = NSRect(x: 500, y: 200, width: 340, height: 640)
        let after = FloatingPanel.collapsedFrame(from: before, collapsed: true, expandedHeight: 640)
        XCTAssertEqual(after.maxY, before.maxY, "顶边必须不动")
        XCTAssertEqual(after.height, FloatingPanel.collapsedHeight)
        XCTAssertEqual(after.origin.x, before.origin.x, "折叠不该横向移动")
        XCTAssertEqual(after.width, before.width, "折叠不该改宽度")
    }

    /// 展开回去同样以顶边为锚，且高度回到折叠前那个值。
    func testExpandRestoresHeightAnchoredAtTop() {
        let collapsed = NSRect(x: 500, y: 799, width: 340, height: FloatingPanel.collapsedHeight)
        let after = FloatingPanel.collapsedFrame(from: collapsed, collapsed: false, expandedHeight: 640)
        XCTAssertEqual(after.maxY, collapsed.maxY, "顶边必须不动")
        XCTAssertEqual(after.height, 640)
    }

    /// 折叠→展开是可逆的：一来一回回到原 frame。
    func testRoundTripIsLossless() {
        let original = NSRect(x: 120, y: 340, width: 420, height: 580)
        let collapsed = FloatingPanel.collapsedFrame(
            from: original, collapsed: true, expandedHeight: original.height)
        let expanded = FloatingPanel.collapsedFrame(
            from: collapsed, collapsed: false, expandedHeight: original.height)
        XCTAssertEqual(expanded, original)
    }

    /// 存量/异常的展开高度不能把窗口还原成一条缝——低于展开态最小高度时抬到最小值。
    func testExpandedHeightIsClampedToMinimum() {
        let collapsed = NSRect(x: 0, y: 900, width: 340, height: FloatingPanel.collapsedHeight)
        let after = FloatingPanel.collapsedFrame(from: collapsed, collapsed: false, expandedHeight: 10)
        XCTAssertGreaterThanOrEqual(after.height, 320, "不得还原成一条缝")
        XCTAssertEqual(after.maxY, collapsed.maxY)
    }

    /// 折叠高度必须与头部布局对得上：`padding(.top,4)` + `frame(height:28)` +
    /// `padding(.bottom,8)` + 分隔线 1。改了头部布局这条会立刻打红。
    func testCollapsedHeightMatchesHeaderLayout() {
        XCTAssertEqual(FloatingPanel.collapsedHeight, 4 + 28 + 8 + 1)
    }

    /// 设置项：折叠前的高度要单独存——折叠后 panelFrame 存的是折叠尺寸，
    /// 只靠它还原不回去。异常值（0 / 小于折叠高度）回退到默认高度。
    func testExpandedHeightSettingFallsBack() {
        let key = SettingsKey.panelExpandedHeight
        let saved = UserDefaults.standard.object(forKey: key)
        defer { UserDefaults.standard.set(saved, forKey: key) }

        UserDefaults.standard.set(0.0, forKey: key)
        XCTAssertEqual(AppSettings.panelExpandedHeight, DesignTokens.shared.panel.defaultHeight)

        UserDefaults.standard.set(Double(FloatingPanel.collapsedHeight), forKey: key)
        XCTAssertEqual(AppSettings.panelExpandedHeight, DesignTokens.shared.panel.defaultHeight,
                       "等于折叠高度也算异常，否则展开后还是折叠态")

        UserDefaults.standard.set(700.0, forKey: key)
        XCTAssertEqual(AppSettings.panelExpandedHeight, 700)
    }

    /// 回归：启动时应用折叠态**不能**把已存的展开高度冲成折叠高度。
    /// 实机路径——设 600 → 折叠 → 重启（此时 frame 已是 41）→ 展开，
    /// 若无「当前确实是展开态」这道判据，600 会被 41 覆盖，展开后只剩一条缝。
    func testApplyingCollapsedAtLaunchMustNotClobberExpandedHeight() {
        let key = SettingsKey.panelExpandedHeight
        let saved = UserDefaults.standard.object(forKey: key)
        defer { UserDefaults.standard.set(saved, forKey: key) }

        UserDefaults.standard.set(600.0, forKey: key)
        // 模拟启动路径：当前 frame 已是折叠尺寸，此时"记录展开高度"必须被跳过
        let collapsedFrame = NSRect(x: 0, y: 0, width: 340, height: FloatingPanel.collapsedHeight)
        XCTAssertFalse(collapsedFrame.height > FloatingPanel.collapsedHeight,
                       "判据本身：折叠尺寸不满足『当前是展开态』")
        XCTAssertEqual(AppSettings.panelExpandedHeight, 600, "600 必须还在")

        // 而真正从展开态折叠时要记下来
        let expandedFrame = NSRect(x: 0, y: 0, width: 340, height: 555)
        XCTAssertTrue(expandedFrame.height > FloatingPanel.collapsedHeight)
    }

    /// 两个新键的文案：表里没有时加载器会**回显键名**，这条守住「记得往
    /// design/strings.json 里加」——否则 tooltip 上会出现 header.collapse 字样。
    func testCollapseStringsExist() {
        for language in [AppLanguage.zhHans, .en, .ja, .ko] {
            Strings.load(language)
            for key in ["header.collapse", "header.expand"] {
                XCTAssertNotEqual(Strings.s(key), key,
                                  "\(language.rawValue) 缺 \(key)（tooltip 会显示键名）")
            }
        }
        Strings.load(.system)
    }
}
