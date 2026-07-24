import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private var panel: FloatingPanel!
    private var settingsWindow: NSWindow?

    private var store: Store!
    private var registry: CodenameRegistry!
    private var watcher: SessionWatcher?
    private var summaryEngine: SummaryEngine!
    private var viewModel: TimelineViewModel!

    func applicationDidFinishLaunching(_ notification: Notification) {
        AppSettings.registerDefaults()

        do {
            store = try Store(path: AppSettings.supportDir + "/store.sqlite")
        } catch {
            NSAlert(error: error).runModal()
            NSApp.terminate(nil)
            return
        }
        registry = CodenameRegistry(store: store)
        viewModel = TimelineViewModel(store: store)
        summaryEngine = SummaryEngine(store: store, registry: registry) {
            NotificationCenter.default.post(name: Store.changedNotification, object: nil)
        }

        setUpPanel()
        setUpStatusItem()
        startWatching()
        summaryEngine.enqueuePendingFromStore()

        NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.panel.applyLevel()
                self?.panel.updateTrackingAndOpacity(animated: true)
            }
        }
    }

    // MARK: - Wiring

    private func startWatching() {
        watcher?.stop()
        let store = self.store!
        let registry = self.registry!
        let engine = self.summaryEngine!
        let rule = RuleSummarizer()
        watcher = SessionWatcher(
            store: store,
            parsers: [ClaudeParser(), CodexParser(), KimiParser(), ZcodeParser()]
        ) { events in
            var newCommands: [UserCommand] = []
            for event in events {
                switch event {
                case .userCommand(let cmd):
                    guard store.insertNodeIfNew(cmd) else { continue }
                    store.setSummary(nodeId: cmd.id, summary: rule.summarize(cmd))
                    registry.recordFromCommand(cmd)
                    newCommands.append(cmd)
                case .assistantText(let agent, let sessionId, let timestamp, let text):
                    let line = ParserSupport.truncate(
                        text.replacingOccurrences(of: "\n", with: " "), to: 160)
                    store.setResultLine(agent: agent, sessionId: sessionId, before: timestamp, line: line)
                }
            }
            if !events.isEmpty {
                NotificationCenter.default.post(name: Store.changedNotification, object: nil)
            }
            if !newCommands.isEmpty {
                engine.enqueue(newCommands)
            }
        }
        watcher?.start()
    }

    /// Called from settings "应用" — restart the watcher (roots may have changed)
    /// and re-kick the summary queue (engine may have changed).
    private func applySettings() {
        startWatching()
        summaryEngine.enqueuePendingFromStore()
        panel.applyLevel()
        panel.updateTrackingAndOpacity(animated: true)
    }

    // MARK: - Panel

    private func setUpPanel() {
        let timelineView = TimelineView(
            viewModel: viewModel,
            onTogglePin: { [weak self] in self?.panel.applyLevel() },
            onOpenSettings: { [weak self] in self?.openSettings() },
            onHide: { [weak self] in self?.panel.orderOut(nil) })
        let hosting = HoverReportingHostingView(rootView: AnyView(timelineView))
        panel = FloatingPanel(contentView: hosting)
        hosting.onHoverChange = { [weak self] hovering in
            self?.panel.setHovering(hovering)
        }
        panel.orderFrontRegardless()
    }

    // MARK: - Status item

    private func setUpStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = statusItem.button {
            button.image = NSImage(
                systemSymbolName: "clock.badge.checkmark",
                accessibilityDescription: "Agent Timeline")
        }
        let menu = NSMenu()
        menu.addItem(withTitle: "显示 / 隐藏时间线", action: #selector(togglePanel), keyEquivalent: "t")
        let pinItem = NSMenuItem(title: "窗口置顶", action: #selector(toggleAlwaysOnTop), keyEquivalent: "")
        menu.addItem(pinItem)
        menu.addItem(.separator())
        menu.addItem(withTitle: "设置…", action: #selector(openSettingsAction), keyEquivalent: ",")
        menu.addItem(.separator())
        menu.addItem(withTitle: "退出 Agent Timeline", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.items.forEach { $0.target = self }
        menu.delegate = self
        statusItem.menu = menu
    }

    @objc private func togglePanel() {
        if panel.isVisible {
            panel.orderOut(nil)
        } else {
            panel.orderFrontRegardless()
        }
    }

    @objc private func toggleAlwaysOnTop() {
        UserDefaults.standard.set(!AppSettings.alwaysOnTop, forKey: SettingsKey.alwaysOnTop)
        panel.applyLevel()
    }

    @objc private func openSettingsAction() {
        openSettings()
    }

    private func openSettings() {
        if settingsWindow == nil {
            let view = SettingsView { [weak self] in self?.applySettings() }
            let window = NSWindow(contentViewController: NSHostingController(rootView: view))
            window.title = "Agent Timeline 设置"
            window.styleMask = [.titled, .closable]
            window.isReleasedWhenClosed = false
            settingsWindow = window
        }
        NSApp.activate(ignoringOtherApps: true)
        settingsWindow?.center()
        settingsWindow?.makeKeyAndOrderFront(nil)
    }
}

extension AppDelegate: NSMenuDelegate {
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.items.first { $0.action == #selector(toggleAlwaysOnTop) }?
            .state = AppSettings.alwaysOnTop ? .on : .off
    }
}
