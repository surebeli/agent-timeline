import AppKit

// Top-level entry runs on the main thread; hop onto the main actor explicitly.
MainActor.assumeIsolated {
    // Two instances sharing one SQLite store lose writes silently — refuse to be second.
    if let bundleId = Bundle.main.bundleIdentifier {
        let others = NSRunningApplication.runningApplications(withBundleIdentifier: bundleId)
            .filter { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }
        if !others.isEmpty {
            exit(0)
        }
    }
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    // Menu-bar widget: no Dock icon, no app switcher entry.
    app.setActivationPolicy(.accessory)
    app.run()
}
