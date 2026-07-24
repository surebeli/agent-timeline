import AppKit

// Top-level entry runs on the main thread; hop onto the main actor explicitly.
MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    // Menu-bar widget: no Dock icon, no app switcher entry.
    app.setActivationPolicy(.accessory)
    app.run()
}
