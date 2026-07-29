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
        // 文案表要在任何界面构建之前载入：菜单栏菜单与代码构建的弹层都在构造期取文案，
        // 晚一步就会拿到键名（与 Windows App.xaml.cs 同一处置）。
        Strings.load(AppSettings.language)

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
        // Watcher and engine start only after the replay finishes, so the two
        // never write the codenames table concurrently.
        replayCodenamesIfNeeded { [weak self] in
            guard let self else { return }
            self.startWatching()
            self.summaryEngine.enqueuePendingFromStore()
        }

        NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.panel.applyLevel()
                self?.panel.updateTrackingAndOpacity(animated: true)
            }
        }
        NotificationCenter.default.addObserver(
            forName: FloatingPanel.holdReadableNotification, object: nil, queue: .main
        ) { [weak self] note in
            MainActor.assumeIsolated {
                guard let self else { return }
                let hold = note.userInfo?["hold"] as? Bool ?? false
                self.panelHoldCount = max(0, self.panelHoldCount + (hold ? 1 : -1))
                self.panel.holdReadable = self.panelHoldCount > 0
            }
        }
    }

    private var panelHoldCount = 0

    // MARK: - Wiring

    /// Bump when detection semantics change enough that history deserves a re-run.
    private static let codenameReplayVersion = 3

    /// One-time per replay version: rebuild the dictionary from stored history
    /// oldest-first so short codes, statuses and definitions light up. The done
    /// marker is written only AFTER completion — a crash mid-replay re-arms.
    private func replayCodenamesIfNeeded(completion: @escaping () -> Void) {
        let key = "codenameReplayVersion"
        guard UserDefaults.standard.integer(forKey: key) < Self.codenameReplayVersion else {
            completion()
            return
        }
        let store = self.store!
        let registry = self.registry!
        DispatchQueue.global(qos: .utility).async {
            store.clearCodenames()
            for node in store.fetchAllNodesAscending() {
                registry.processCommand(node.command)
                if let summary = node.summary {
                    registry.recordFromSummary(summary, nodeId: node.id, seenAt: node.command.timestamp)
                    if let result = summary.resultLine, !result.isEmpty {
                        registry.processAssistantText(result, nodeId: node.id, at: node.command.timestamp)
                    }
                }
            }
            DispatchQueue.main.async {
                UserDefaults.standard.set(Self.codenameReplayVersion, forKey: key)
                NotificationCenter.default.post(name: Store.changedNotification, object: nil)
                completion()
            }
        }
    }

    private func startWatching() {
        watcher?.stop()
        let store = self.store!
        let registry = self.registry!
        let engine = self.summaryEngine!
        let rule = RuleSummarizer()
        watcher = SessionWatcher(
            store: store,
            parsers: [ClaudeParser(), CodexParser(), GrokParser(), KimiParser(), ZcodeParser()]
        ) { events in
            var newCommands: [UserCommand] = []
            for event in events {
                switch event {
                case .userCommand(let cmd):
                    guard store.insertNodeIfNew(cmd) else { continue }
                    store.setSummary(nodeId: cmd.id, summary: rule.summarize(cmd))
                    registry.processCommand(cmd)
                    newCommands.append(cmd)
                case .assistantText(let agent, let sessionId, let timestamp, let text):
                    // 规整 → 首段 → ≤500（docs/TEXT-NORMALIZATION.md §3.1 Excerpt 档）。
                    // 代号挖掘吃的仍是未规整全文（下方 processAssistantText）。
                    let line = ParserSupport.resultExcerpt(text)
                    store.setResultLine(agent: agent, sessionId: sessionId, before: timestamp, line: line)
                    // Definitions often live in the reply ("好的，编号如下：N1: …").
                    if let nodeId = store.latestNodeId(agent: agent, sessionId: sessionId, before: timestamp) {
                        registry.processAssistantText(text, nodeId: nodeId, at: timestamp)
                    }
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
        store.resetSummaryAttempts()  // engine may have changed; give failures a fresh chance
        summaryEngine.enqueuePendingFromStore()
        panel.applyLevel()
        panel.updateTrackingAndOpacity(animated: true)
    }

    // MARK: - Panel

    private func setUpPanel() {
        let timelineView = TimelineView(
            viewModel: viewModel,
            onTogglePin: { [weak self] in self?.panel.applyLevel() },
            onOpenSettings: { [weak self] in self?.openSettings() })
        let hosting = HoverReportingHostingView(rootView: AnyView(timelineView))
        // SwiftUI 默认为标题栏留安全区，会把头部整体下压一个标题栏高度，
        // 交通灯与标题被迫分成两行。挂件竖向空间寸土寸金——让内容顶到窗口顶，
        // 头部与交通灯同排（原生 Safari/Finder 工具栏就是这个关系）。
        hosting.safeAreaRegions = []
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
            // 版本信息统一放在设置窗 caption（双端同文案）
            let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
            window.title = "Agent Timeline 设置 · v\(version)"
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
