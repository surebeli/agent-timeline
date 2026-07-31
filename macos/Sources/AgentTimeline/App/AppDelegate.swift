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
        // 默认开机自启动：registerDefaults 已把偏好设成 true（新装/未碰过这项的老用户
        // 都适用），这里把系统实际登录项状态同步过去。系统状态可能因用户在「系统设置 >
        // 通用 > 登录项」里手动改过而跟偏好脱钩，LoginItem.sync 只在真的不一致时才动。
        LoginItem.sync(desired: AppSettings.launchAtLogin)

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
        setUpKeyboardShortcuts()
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
        // 菜单栏菜单是代码构建的，不会随语言自动刷新——切换后整体重建。
        // 设置窗的标题栏同理：NSWindow.title 是命令式赋值，SwiftUI 的 body 重算救不到它，
        // 窗口本身又常驻不释放（isReleasedWhenClosed = false），不重设标题会一直停留在
        // 打开那一刻的语言（实机拍引导图时发现：标题栏"设置"、正文却是英文的混语状态）。
        NotificationCenter.default.addObserver(
            forName: Strings.didChangeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.rebuildStatusMenu()
                self?.refreshSettingsWindowTitle()
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
            onToggleCollapse: { [weak self] in
                guard let self else { return }
                self.panel.setCollapsed(!self.panel.isCollapsed, animated: true)
            },
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
        // 折叠态跨重启保持：panelFrame 存的已经是折叠尺寸，这里只需把标志与约束对上，
        // 故不做动画、也不重算 frame（animated:false + 当前 frame 即目标）。
        if AppSettings.panelCollapsed {
            panel.setCollapsed(true, animated: false)
        }
        panel.orderFrontRegardless()
    }

    // MARK: - Status item

    /// `.accessory` 策略的 app 没有 Dock 图标，也从没设过 `NSApp.mainMenu`——没有任何
    /// 菜单栏条目能承载 Cmd+Q / Cmd+W / Cmd+, 这类系统标准快捷键，面板或设置窗拿到
    /// 焦点按下去都不会有反应。用本地事件监听补上，效果与常规 app 的「App 菜单 → 退出 /
    /// 偏好设置」「文件菜单 → 关闭窗口」等价。
    ///
    /// ⚠ 这跟托盘菜单项上挂的 `keyEquivalent`（"q"/","/之前的 "t"）是两回事：那些只是
    /// 菜单里的**显示标签**，只有装进 `NSApp.mainMenu` 的菜单项才会被系统当成全局快捷键
    /// 处理——状态栏菜单从来没有这个资格，标签好看但从没生效过。真正生效的只有这里的
    /// 本地监听；"t"（显示/隐藏）那条已经确认从来没起过作用，直接把标签也摘掉了，
    /// 见 `rebuildStatusMenu()`。
    private func setUpKeyboardShortcuts() {
        NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self else { return event }
            guard event.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command
            else { return event }
            switch event.charactersIgnoringModifiers {
            case "q":
                NSApp.terminate(nil)
                return nil
            case "w":
                // 按谁是 key window 决定语义：设置窗关闭、面板隐藏，跟系统标准 app
                // 「Cmd+W 关活动窗口」的直觉一致，不是两个窗口共用同一个动作。
                if let settingsWindow = self.settingsWindow, settingsWindow.isKeyWindow {
                    settingsWindow.performClose(nil)
                    return nil
                }
                if self.panel.isKeyWindow {
                    self.panel.orderOut(nil)
                    return nil
                }
                return event
            case ",":
                // 顺手把这条也接上：托盘菜单「设置…」项标着 ⌘, 但同样只是标签，
                // 跟 "t" 是同一类问题，且 Cmd+, 是 mac 上开偏好设置的标准约定。
                self.openSettings()
                return nil
            default:
                return event
            }
        }
    }

    private func setUpStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = statusItem.button {
            button.image = NSImage(
                systemSymbolName: "clock.badge.checkmark",
                accessibilityDescription: "Agent Timeline")
        }
        rebuildStatusMenu()
    }

    /// 菜单项标题在构造期取文案，语言切换后必须整体重建（AppKit 菜单不会自刷新）。
    private func rebuildStatusMenu() {
        guard statusItem != nil else { return }
        let menu = NSMenu()
        menu.addItem(withTitle: Strings.s("tray.showHide"), action: #selector(togglePanel), keyEquivalent: "")
        let pinItem = NSMenuItem(title: Strings.s("tray.alwaysOnTop"), action: #selector(toggleAlwaysOnTop), keyEquivalent: "")
        menu.addItem(pinItem)
        menu.addItem(.separator())
        menu.addItem(withTitle: Strings.s("tray.settings"), action: #selector(openSettingsAction), keyEquivalent: ",")
        menu.addItem(.separator())
        let quitItem = NSMenuItem(
            title: Strings.s("tray.exit"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.addItem(quitItem)
        // ⚠ 别把这条也塞进下面那个 target=self 循环：terminate(_:) 是 NSApplication 的方法，
        // AppDelegate 没有实现它。target 一旦显式指向不响应该 selector 的对象，AppKit 的
        // 自动菜单校验会直接把这一项置灰——退出菜单会跟着这个循环一起悄悄失效
        // （长期存在的 bug：从最初加这个菜单起就这样，一直没人点过退出才没被发现）。
        quitItem.target = NSApp
        menu.items.forEach { if $0 !== quitItem { $0.target = self } }
        menu.delegate = self
        statusItem.menu = menu
    }

    @objc private func togglePanel() {
        if panel.isVisible {
            panel.orderOut(nil)
        } else {
            // 折叠态跨重启保持：panelFrame 存的已经是折叠尺寸，这里只需把标志与约束对上，
        // 故不做动画、也不重算 frame（animated:false + 当前 frame 即目标）。
        if AppSettings.panelCollapsed {
            panel.setCollapsed(true, animated: false)
        }
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
            window.title = Strings.f("app.settingsTitle", version)
            window.styleMask = [.titled, .closable]
            window.isReleasedWhenClosed = false
            settingsWindow = window
        }
        NSApp.activate(ignoringOtherApps: true)
        settingsWindow?.center()
        settingsWindow?.makeKeyAndOrderFront(nil)
    }

    private func refreshSettingsWindowTitle() {
        guard let window = settingsWindow else { return }
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        window.title = Strings.f("app.settingsTitle", version)
    }
}

extension AppDelegate: NSMenuDelegate {
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.items.first { $0.action == #selector(toggleAlwaysOnTop) }?
            .state = AppSettings.alwaysOnTop ? .on : .off
    }
}
