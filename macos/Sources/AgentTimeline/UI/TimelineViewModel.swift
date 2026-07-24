import Foundation
import Observation

@MainActor
@Observable
final class TimelineViewModel {
    private(set) var nodes: [TimelineNode] = []
    private(set) var codenames: [String: CodenameEntry] = [:]
    var projectFilter: String?
    var agentFilter: Set<AgentKind> = Set(AgentKind.allCases)
    var expanded: Set<String> = []
    var scrollTarget: String?

    private let store: Store
    private var reloadScheduled = false
    private var fetchLimit = 500

    var canLoadMore: Bool { nodes.count >= fetchLimit }

    init(store: Store) {
        self.store = store
        NotificationCenter.default.addObserver(
            forName: Store.changedNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.scheduleReload() }
        }
        reload()
    }

    var projects: [String] {
        var seen = Set<String>()
        return nodes.compactMap { node in
            seen.insert(node.command.project).inserted ? node.command.project : nil
        }
    }

    var visibleNodes: [TimelineNode] {
        nodes.filter { node in
            agentFilter.contains(node.command.agent)
                && (projectFilter == nil || node.command.project == projectFilter)
        }
    }

    func entry(forCodename name: String) -> CodenameEntry? {
        codenames[name]
    }

    func jumpToDefinition(of name: String) {
        guard let entry = codenames[name], !entry.definitionNodeId.isEmpty else { return }
        // The definition node may be hidden by an active filter — clear them first.
        projectFilter = nil
        agentFilter = Set(AgentKind.allCases)
        // …or lie beyond the current fetch window (long backfills).
        if !nodes.contains(where: { $0.id == entry.definitionNodeId }) {
            fetchLimit = 100_000
            reload()
        }
        expanded.insert(entry.definitionNodeId)
        scrollTarget = entry.definitionNodeId
    }

    func loadMore() {
        fetchLimit += 500
        reload()
    }

    func reload() {
        nodes = store.fetchNodes(limit: fetchLimit)
        codenames = store.fetchCodenames()
    }

    private func scheduleReload() {
        guard !reloadScheduled else { return }
        reloadScheduled = true
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(300))
            reloadScheduled = false
            reload()
        }
    }
}
