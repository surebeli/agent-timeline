import CoreServices
import Foundation

/// Watches agent session roots via FSEvents and tails changed files
/// incrementally: per-file byte offsets persist in the store (only advanced past
/// complete lines, so partial writes survive an app restart), and an inode
/// change resets the file.
final class SessionWatcher: @unchecked Sendable {
    private let store: Store
    private let parsers: [AgentSessionParser]
    private let onEvents: ([SessionEvent]) -> Void

    private let workQueue = DispatchQueue(label: "agent-timeline.watcher", qos: .utility)
    private var stream: FSEventStreamRef?
    private var contexts: [String: ParsedFileContext] = [:]
    private var pendingPaths = Set<String>()
    private var flushScheduled = false
    /// Safety net for writes FSEvents coalesces away: files touched recently get re-polled.
    private var pollTimer: DispatchSourceTimer?
    private var recentFiles: [String: Date] = [:]

    init(store: Store, parsers: [AgentSessionParser], onEvents: @escaping ([SessionEvent]) -> Void) {
        self.store = store
        self.parsers = parsers
        self.onEvents = onEvents
    }

    func start() {
        let roots = parsers
            .filter { AppSettings.isAgentEnabled($0.agent) }
            .flatMap { $0.watchRoots() }
            .filter { FileManager.default.fileExists(atPath: $0.path) }
        guard !roots.isEmpty else { return }

        var fsContext = FSEventStreamContext(
            version: 0,
            info: Unmanaged.passUnretained(self).toOpaque(),
            retain: nil, release: nil, copyDescription: nil)
        let callback: FSEventStreamCallback = { _, info, count, paths, _, _ in
            guard let info else { return }
            let watcher = Unmanaged<SessionWatcher>.fromOpaque(info).takeUnretainedValue()
            let cPaths = paths.assumingMemoryBound(to: UnsafeMutablePointer<CChar>.self)
            var changed: [String] = []
            for i in 0..<count {
                changed.append(String(cString: cPaths[i]))
            }
            watcher.enqueue(paths: changed)
        }
        stream = FSEventStreamCreate(
            kCFAllocatorDefault, callback, &fsContext,
            roots.map(\.path) as CFArray,
            FSEventStreamEventId(kFSEventStreamEventIdSinceNow),
            0.5,
            FSEventStreamCreateFlags(kFSEventStreamCreateFlagFileEvents | kFSEventStreamCreateFlagNoDefer))
        if let stream {
            FSEventStreamSetDispatchQueue(stream, workQueue)
            FSEventStreamStart(stream)
        }

        let timer = DispatchSource.makeTimerSource(queue: workQueue)
        timer.schedule(deadline: .now() + 15, repeating: 15)
        timer.setEventHandler { [weak self] in self?.pollRecent() }
        timer.resume()
        pollTimer = timer

        workQueue.async { [weak self] in self?.backfill(roots: roots) }
    }

    func stop() {
        // Tear down on workQueue so no FSEvents callback can be mid-flight when the
        // caller deallocates us (context.info is an unretained pointer to self).
        workQueue.sync {
            if let stream = self.stream {
                FSEventStreamStop(stream)
                FSEventStreamInvalidate(stream)
                FSEventStreamRelease(stream)
            }
            self.stream = nil
            self.pollTimer?.cancel()
            self.pollTimer = nil
        }
    }

    // MARK: - Change intake

    private func enqueue(paths: [String]) {
        workQueue.async { [weak self] in
            guard let self else { return }
            for path in paths {
                self.pendingPaths.insert(path)
            }
            if !self.flushScheduled {
                self.flushScheduled = true
                self.workQueue.asyncAfter(deadline: .now() + 0.3) { [weak self] in
                    self?.flushPending()
                }
            }
        }
    }

    private func flushPending() {
        flushScheduled = false
        let paths = pendingPaths
        pendingPaths.removeAll()
        for path in paths.sorted() {
            var isDir: ObjCBool = false
            guard FileManager.default.fileExists(atPath: path, isDirectory: &isDir) else { continue }
            if isDir.boolValue {
                // New session directories (e.g. kimi creates one per session) —
                // scan for parseable files inside.
                scanDirectory(URL(fileURLWithPath: path), newerThan: Date().addingTimeInterval(-3600))
            } else {
                processFile(URL(fileURLWithPath: path))
            }
        }
    }

    private func pollRecent() {
        let cutoff = Date().addingTimeInterval(-2 * 3600)
        recentFiles = recentFiles.filter { $0.value > cutoff }
        for path in recentFiles.keys {
            processFile(URL(fileURLWithPath: path), silentIfMissing: true)
        }
    }

    // MARK: - Backfill

    private func backfill(roots: [URL]) {
        let days = max(AppSettings.backfillDays, 1)
        let cutoff = Date().addingTimeInterval(-Double(days) * 86400)
        for root in roots {
            scanDirectory(root, newerThan: cutoff)
        }
    }

    private func scanDirectory(_ dir: URL, newerThan cutoff: Date) {
        let keys: [URLResourceKey] = [.contentModificationDateKey, .isRegularFileKey]
        guard let enumerator = FileManager.default.enumerator(
            at: dir, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles]) else { return }
        var files: [(URL, Date)] = []
        for case let url as URL in enumerator {
            guard let values = try? url.resourceValues(forKeys: Set(keys)),
                  values.isRegularFile == true,
                  let mtime = values.contentModificationDate,
                  mtime > cutoff else { continue }
            files.append((url, mtime))
        }
        for (url, _) in files.sorted(by: { $0.1 < $1.1 }) {
            processFile(url)
        }
    }

    // MARK: - Incremental tail

    private func processFile(_ url: URL, silentIfMissing: Bool = false) {
        let path = url.path

        // Resolve a parser context (cached per path).
        var context: ParsedFileContext
        if let cached = contexts[path] {
            context = cached
        } else {
            guard let fresh = parsers
                .filter({ AppSettings.isAgentEnabled($0.agent) })
                .compactMap({ $0.makeContext(for: url) })
                .first else { return }
            context = fresh
        }
        if context.disabled { contexts[path] = context; return }

        guard let attrs = try? FileManager.default.attributesOfItem(atPath: path),
              let sizeNum = attrs[.size] as? NSNumber,
              let inodeNum = attrs[.systemFileNumber] as? NSNumber else {
            if !silentIfMissing { contexts[path] = nil }
            return
        }
        let inode = inodeNum.uint64Value
        let size = sizeNum.int64Value

        var offset: Int64 = 0
        if let saved = store.fileOffset(path: path), saved.inode == inode, saved.offset <= size {
            offset = saved.offset
        }
        guard size > offset else {
            if offset > size {
                // Truncated in place: start over.
                store.setFileOffset(path: path, offset: 0, inode: inode)
            }
            return
        }

        guard let handle = try? FileHandle(forReadingFrom: url) else { return }
        defer { try? handle.close() }
        do {
            try handle.seek(toOffset: UInt64(offset))
        } catch { return }
        guard let newData = try? handle.readToEnd(), !newData.isEmpty else { return }

        var buffer = newData
        var events: [SessionEvent] = []
        while let nl = buffer.firstIndex(of: 0x0A) {
            let lineData = buffer.subdata(in: buffer.startIndex..<nl)
            buffer.removeSubrange(buffer.startIndex...nl)
            if let line = String(data: lineData, encoding: .utf8), !line.isEmpty {
                events.append(contentsOf: context.parse(line: line, parsers: parsers))
                if context.disabled { break }
            }
        }
        contexts[path] = context
        recentFiles[path] = Date()
        // Only advance past complete lines; a trailing partial line is re-read next time.
        let consumed = context.disabled ? size : size - Int64(buffer.count)
        store.setFileOffset(path: path, offset: consumed, inode: inode)

        if !events.isEmpty {
            onEvents(events)
        }
    }
}

private extension ParsedFileContext {
    /// Dispatch a line to the parser owning this context's agent.
    mutating func parse(line: String, parsers: [AgentSessionParser]) -> [SessionEvent] {
        guard let parser = parsers.first(where: { $0.agent == agent }) else { return [] }
        return parser.parse(line: line, context: &self)
    }
}
