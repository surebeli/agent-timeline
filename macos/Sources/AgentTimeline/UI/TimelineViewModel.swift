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
        expanded.insert(entry.definitionNodeId)
        scrollTarget = entry.definitionNodeId
    }

    func reload() {
        nodes = store.fetchNodes()
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
