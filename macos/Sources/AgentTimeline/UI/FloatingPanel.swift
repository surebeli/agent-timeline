import AppKit
import SwiftUI

/// The translucent timeline panel. Non-activating so clicking it never steals
/// focus from the frontmost app; it may still become key (for text selection)
/// without activating us. Opacity follows hover/key state:
/// readable (~0.95) while hovered or key, near-transparent (~0.25) otherwise.
final class FloatingPanel: NSPanel {
    /// Posted (userInfo["hold": Bool]) while a popover/menu anchored in the panel
    /// is open, so the panel stays readable even though the mouse left it.
    static let holdReadableNotification = Notification.Name("PanelHoldReadable")

    private var hovering = false

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
            styleMask: [.nonactivatingPanel, .titled, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false)

        titleVisibility = .hidden
        titlebarAppearsTransparent = true
        standardWindowButton(.closeButton)?.isHidden = true
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
        minSize = NSSize(width: tokens.panel.minWidth, height: 320)
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
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = DesignTokens.shared.opacity.transitionMs / 1000
                animator().alphaValue = clamped
            }
        } else {
            alphaValue = clamped
        }
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
            if rect.width > 100, rect.height > 100 {
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
