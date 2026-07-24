import Foundation

/// Serial summarization pipeline. New commands get an instant rule summary at
/// insert time (elsewhere); this engine upgrades them to LLM summaries one at a
/// time — newest first — through the configured backend, with per-node attempt
/// caps and a small delay between calls so the local CLI is never hammered.
final class SummaryEngine: @unchecked Sendable {
    private let store: Store
    private let registry: CodenameRegistry
    private let onUpdated: @Sendable () -> Void

    private let workQueue = DispatchQueue(label: "agent-timeline.summary", qos: .utility)
    private var pending: [UserCommand] = []
    private var queuedIds = Set<String>()
    private var processing = false
    private var resolvedCLI: CLISummarizer.ResolvedCLI?
    private var cliResolutionDone = false

    init(store: Store, registry: CodenameRegistry, onUpdated: @escaping @Sendable () -> Void) {
        self.store = store
        self.registry = registry
        self.onUpdated = onUpdated
        NotificationCenter.default.addObserver(
            forName: UserDefaults.didChangeNotification, object: nil, queue: nil
        ) { [weak self] _ in
            self?.workQueue.async { self?.cliResolutionDone = false }
        }
    }

    func enqueue(_ commands: [UserCommand]) {
        guard !commands.isEmpty else { return }
        workQueue.async { [weak self] in
            guard let self else { return }
            for cmd in commands where !self.queuedIds.contains(cmd.id) {
                self.queuedIds.insert(cmd.id)
                self.pending.append(cmd)
            }
            self.pending.sort { $0.timestamp < $1.timestamp }  // popLast() → newest first
            self.schedule()
        }
    }

    /// Pick up anything the store still marks unsummarized (app restart, engine switch).
    func enqueuePendingFromStore() {
        workQueue.async { [weak self] in
            guard let self else { return }
            self.cliResolutionDone = false
            let cmds = self.store.pendingSummaries()
            for cmd in cmds where !self.queuedIds.contains(cmd.id) {
                self.queuedIds.insert(cmd.id)
                self.pending.append(cmd)
            }
            self.pending.sort { $0.timestamp < $1.timestamp }
            self.schedule()
        }
    }

    // MARK: - Worker (all on workQueue)

    private func schedule() {
        guard !processing else { return }
        processing = true
        processNext()
    }

    private enum BackendOutcome {
        case success(Summary)
        case failed
        /// No backend can run at all (no CLI found / provider unconfigured):
        /// don't burn the node's attempts — it gets retried after settings change.
        case unavailable
    }

    private func processNext() {
        guard AppSettings.engineMode != .rule else {
            pending.removeAll()
            queuedIds.removeAll()
            processing = false
            return
        }
        guard let cmd = pending.popLast() else {
            processing = false
            return
        }
        queuedIds.remove(cmd.id)

        switch runBackend(for: cmd) {
        case .success(let summary):
            store.setSummary(nodeId: cmd.id, summary: summary)
            registry.recordFromSummary(summary, nodeId: cmd.id, seenAt: cmd.timestamp)
            onUpdated()
        case .failed:
            if store.bumpSummaryAttempts(nodeId: cmd.id) < 3 {
                queuedIds.insert(cmd.id)
                pending.insert(cmd, at: 0)  // retry later, after newer work
            }
        case .unavailable:
            // Put it back and stall the queue; a settings change re-kicks us.
            queuedIds.insert(cmd.id)
            pending.append(cmd)
            processing = false
            return
        }
        workQueue.asyncAfter(deadline: .now() + 1.0) { [weak self] in
            self?.processNext()
        }
    }

    private func runBackend(for cmd: UserCommand) -> BackendOutcome {
        switch AppSettings.engineMode {
        case .rule:
            return .unavailable
        case .cli:
            if !cliResolutionDone {
                resolvedCLI = CLISummarizer.resolve()
                cliResolutionDone = true
            }
            guard let cli = resolvedCLI else { return .unavailable }
            if let summary = try? CLISummarizer(cli: cli, model: AppSettings.cliModel).summarize(cmd) {
                return .success(summary)
            }
            return .failed
        case .provider:
            guard !AppSettings.providerBaseURL.isEmpty, !AppSettings.providerModel.isEmpty else {
                return .unavailable
            }
            let provider = ProviderSummarizer(
                baseURL: AppSettings.providerBaseURL,
                apiKey: AppSettings.providerAPIKey,
                model: AppSettings.providerModel)
            let semaphore = DispatchSemaphore(value: 0)
            nonisolated(unsafe) var outcome: Summary?
            Task.detached {
                outcome = try? await provider.summarize(cmd)
                semaphore.signal()
            }
            semaphore.wait()
            if let outcome { return .success(outcome) }
            return .failed
        }
    }
}
