import AppKit
import QuartzCore   // CAMediaTimingFunction（淡入/淡出用不同曲线）
import SwiftUI

/// The translucent timeline panel. Non-activating so clicking it never steals
/// focus from the frontmost app; it may still become key (for text selection)
/// without activating us. Opacity follows hover/key state:
/// readable (~0.95) while hovered or key, near-transparent (~0.25) otherwise.
final class FloatingPanel: NSPanel, NSWindowDelegate {
    /// Posted (userInfo["hold": Bool]) while a popover/menu anchored in the panel
    /// is open, so the panel stays readable even though the mouse left it.
    static let holdReadableNotification = Notification.Name("PanelHoldReadable")

    /// 折叠态的窗口高度：只留 caption 那一行。
    ///
    /// = 头部 `padding(.top, 4)` + `frame(height: 28)` + `padding(.bottom, 8)` + 分隔线 1
    /// （见 `TimelineView.body`）。这三个数与 Windows 侧头部布局同源，改一处要两端同改。
    static let collapsedHeight: CGFloat = 41

    /// 展开态的最小高度。折叠是**显式操作**，不能让人用拖拽把窗口缩到折叠尺寸——
    /// 那样 collapsed 标志与实际高度就脱钩了。
    private static let expandedMinHeight: CGFloat = 320

    private var hovering = false

    private(set) var isCollapsed = false

    var holdReadable = false {
        didSet { updateTrackingAndOpacity(animated: true) }
    }

    init(contentView: NSView) {
        let tokens = DesignTokens.shared
        let frame = NSRect(
            x: 0, y: 0,
            width: tokens.panel.defaultWidth, height: tokens.panel.defaultHeight)
        super.init(
            contentRect: frame,
            styleMask: [.nonactivatingPanel, .titled, .closable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false)

        // 原生 caption：真交通灯（只留关闭），不是自绘图标。
        // 实测本窗类可用性——close：加 .closable 即可用；miniaturize：NSPanel 默认
        // 禁用，且挂件没有 Dock 图标，最小化语义上无处可去；zoom：把半透明侧栏
        // 时间线"最大化"没有意义。macOS 自家的工具面板（字体面板、检查器）同样
        // 只给关闭，所以这里保持一致而不是硬凑三颗。
        title = "Agent Timeline"      // 隐藏显示，但让 Mission Control / 截图选择器认得出
        titleVisibility = .hidden
        titlebarAppearsTransparent = true
        standardWindowButton(.miniaturizeButton)?.isHidden = true
        standardWindowButton(.zoomButton)?.isHidden = true

        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        isMovableByWindowBackground = true
        hidesOnDeactivate = false
        becomesKeyOnlyIfNeeded = true
        isFloatingPanel = true
        animationBehavior = .utilityWindow
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        minSize = NSSize(width: tokens.panel.minWidth, height: Self.expandedMinHeight)
        maxSize = NSSize(width: tokens.panel.maxWidth, height: 4000)

        // Blur backdrop + rounded corners, content hosted above it.
        let effect = NSVisualEffectView(frame: frame)
        effect.material = .hudWindow
        effect.blendingMode = .behindWindow
        effect.state = .active
        effect.wantsLayer = true
        effect.layer?.cornerRadius = tokens.radius.panel
        effect.layer?.masksToBounds = true
        effect.autoresizingMask = [.width, .height]

        contentView.frame = effect.bounds
        contentView.autoresizingMask = [.width, .height]
        effect.addSubview(contentView)
        self.contentView = effect

        delegate = self     // 自任 delegate：windowShouldClose 走「收回菜单栏」
        applyLevel()
        restoreFrame()

        NotificationCenter.default.addObserver(
            self, selector: #selector(keyStateChanged),
            name: NSWindow.didBecomeKeyNotification, object: self)
        NotificationCenter.default.addObserver(
            self, selector: #selector(keyStateChanged),
            name: NSWindow.didResignKeyNotification, object: self)
        NotificationCenter.default.addObserver(
            self, selector: #selector(persistFrame),
            name: NSWindow.didMoveNotification, object: self)
        NotificationCenter.default.addObserver(
            self, selector: #selector(persistFrame),
            name: NSWindow.didResizeNotification, object: self)

        updateTrackingAndOpacity(animated: false)
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    /// 菜单栏挂件的原生语义：关闭 = 收回菜单栏，进程继续驻留。
    /// 挂在 windowShouldClose 上，⌘W 与交通灯走的是同一条路径（原生一致）。
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        orderOut(nil)
        return false
    }

    // MARK: - Hover / focus driven opacity

    func setHovering(_ value: Bool) {
        guard hovering != value else { return }
        hovering = value
        updateTrackingAndOpacity(animated: true)
    }

    @objc private func keyStateChanged() {
        updateTrackingAndOpacity(animated: true)
    }

    /// Mouse-exit never fires for a hidden panel — clear hover state on hide so
    /// the next show doesn't come back stuck at full opacity.
    override func orderOut(_ sender: Any?) {
        hovering = false
        super.orderOut(sender)
        updateTrackingAndOpacity(animated: false)
    }

    func updateTrackingAndOpacity(animated: Bool) {
        let readable = hovering || isKeyWindow || holdReadable
        let target = readable ? AppSettings.hoverOpacity : AppSettings.idleOpacity
        let clamped = max(0.05, min(1.0, target))
        if animated {
            // 快淡入、慢淡出：指针一进来要立刻可读，移开时则从容化开，避免
            // "看到一半就唰地消失"。方向同时决定时长与曲线——只拉长时长而不换
            // 曲线的话，easeOut 会把绝大部分变化挤在前段，观感反而是"唰一下再
            // 慢慢爬"。与 Windows OpacityAnimator 同一套语义。
            let fadingOut = clamped < alphaValue
            let tokens = DesignTokens.shared.opacity
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = (fadingOut ? tokens.transitionOutMs : tokens.transitionMs) / 1000
                ctx.timingFunction = CAMediaTimingFunction(name: fadingOut ? .easeIn : .easeOut)
                animator().alphaValue = clamped
            }
        } else {
            alphaValue = clamped
        }
    }

    /// 折叠/展开后的目标 frame。**顶边不动**——挂件通常贴着屏幕某处放，折叠时应该像
    /// 卷帘一样往上收，而不是原地缩到一半再跳。纯函数，供单测直接验几何。
    static func collapsedFrame(
        from frame: NSRect, collapsed: Bool, expandedHeight: CGFloat
    ) -> NSRect {
        let target = collapsed ? collapsedHeight : max(expandedMinHeight, expandedHeight)
        var out = frame
        out.origin.y += frame.height - target      // 顶边 = origin.y + height 保持不变
        out.size.height = target
        return out
    }

    /// 折叠到只剩 caption / 还原。折叠态**锁住竖向尺寸**（min=max=collapsedHeight），
    /// 否则可以拖着边缘把「折叠中」的窗口拉高，标志与实际高度脱钩。
    func setCollapsed(_ collapsed: Bool, animated: Bool) {
        // 折叠前把当前高度记下来：折叠后 persistFrame 存进 panelFrame 的是折叠尺寸。
        // ⚠ 必须先确认当前**确实是展开态**：启动时若上次是折叠的，restoreFrame 还原的就是
        // 41pt 的帧，这里再无条件记一次就把用户真正的高度冲成 41——展开后只剩一条缝。
        // （实机测出来的：设 600、折叠、重启、展开，回到的是默认 640 而不是 600，
        // 说明 600 已经被 41 覆盖、只是被 AppSettings 的回退兜住了。）
        if collapsed, !isCollapsed, frame.height > Self.collapsedHeight {
            UserDefaults.standard.set(frame.height, forKey: SettingsKey.panelExpandedHeight)
        }
        isCollapsed = collapsed
        UserDefaults.standard.set(collapsed, forKey: SettingsKey.panelCollapsed)

        let target = Self.collapsedFrame(
            from: frame, collapsed: collapsed,
            expandedHeight: CGFloat(AppSettings.panelExpandedHeight))
        // 先放开约束再改 frame，否则 minSize 会把折叠挡住
        minSize = NSSize(
            width: minSize.width,
            height: collapsed ? Self.collapsedHeight : Self.expandedMinHeight)
        maxSize = NSSize(
            width: maxSize.width,
            height: collapsed ? Self.collapsedHeight : 4000)
        setFrame(target, display: true, animate: animated)
    }

    func applyLevel() {
        level = AppSettings.alwaysOnTop ? .floating : .normal
    }

    // MARK: - Frame persistence

    private static let frameKey = "panelFrame"

    @objc private func persistFrame() {
        UserDefaults.standard.set(NSStringFromRect(frame), forKey: Self.frameKey)
    }

    private func restoreFrame() {
        if let saved = UserDefaults.standard.string(forKey: Self.frameKey) {
            let rect = NSRectFromString(saved)
            // 下界取折叠高度而不是写死 100：折叠态存下来的就是 41pt，用 100 会把它判成
            // 垃圾帧、退回「贴主屏右缘 + 默认宽」的默认分支——折叠位置与宽度一起丢。
            // （这条是加折叠功能时实机测出来的：重启后 430×41 变成了 340×41 并跳到屏幕右上。）
            if rect.width > 100, rect.height >= Self.collapsedHeight {
                setFrame(rect, display: false)
                return
            }
        }
        // Default: right edge of the main screen.
        if let screen = NSScreen.main {
            let f = frame
            let x = screen.visibleFrame.maxX - f.width - 24
            let y = screen.visibleFrame.maxY - f.height - 24
            setFrameOrigin(NSPoint(x: x, y: y))
        }
    }
}

/// Hosts SwiftUI content and reports mouse enter/exit to the panel.
final class HoverReportingHostingView<Content: View>: NSHostingView<Content> {
    var onHoverChange: ((Bool) -> Void)?
    private var trackingArea: NSTrackingArea?

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let area = NSTrackingArea(
            rect: bounds,
            options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
            owner: self, userInfo: nil)
        addTrackingArea(area)
        trackingArea = area
    }

    override func mouseEntered(with event: NSEvent) {
        super.mouseEntered(with: event)
        onHoverChange?(true)
    }

    override func mouseExited(with event: NSEvent) {
        super.mouseExited(with: event)
        onHoverChange?(false)
    }

    override func mouseDown(with event: NSEvent) {
        // Let the panel become key so text selection works, without activating the app.
        window?.makeKey()
        super.mouseDown(with: event)
    }
}
