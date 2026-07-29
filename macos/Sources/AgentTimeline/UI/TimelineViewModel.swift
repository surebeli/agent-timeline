import Foundation
import Observation

@MainActor
@Observable
final class TimelineViewModel {
    private(set) var nodes: [TimelineNode] = []
    private(set) var codenames: [String: CodenameEntry] = [:]
    var projectFilter: String?
    var agentFilter: Set<AgentKind> = Set(AgentKind.allCases)
    /// NodeKind rawValue; nil = all phases.
    var kindFilter: String?
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

    /// Most-recently-active agent per project — drives the project dropdown's
    /// source badge (parity with the Windows project filter).
    var projectRecentAgents: [String: AgentKind] {
        Self.recentAgentByProject(nodes)
    }

    /// Pure helper: `nodes` must be newest-first (as stored); first sighting wins.
    static func recentAgentByProject(_ nodes: [TimelineNode]) -> [String: AgentKind] {
        var out: [String: AgentKind] = [:]
        for node in nodes where out[node.command.project] == nil {
            out[node.command.project] = node.command.agent
        }
        return out
    }

    var visibleNodes: [TimelineNode] {
        nodes.filter { node in
            agentFilter.contains(node.command.agent)
                && (projectFilter == nil || node.command.project == projectFilter)
                && (kindFilter == nil || node.summary?.kind == kindFilter)
        }
    }

    /// Dictionary panel ordering: recently updated first, then recently seen.
    var sortedCodenames: [CodenameEntry] {
        codenames.values.sorted {
            ($0.updated ?? $0.firstSeen) > ($1.updated ?? $1.firstSeen)
        }
    }

    /// Nodes that first defined a codename get the accent ring on their rail marker.
    var definitionNodeIds: Set<String> {
        Set(codenames.values.map(\.definitionNodeId).filter { !$0.isEmpty })
    }

    struct DayGroup: Identifiable {
        let label: String
        let nodes: [TimelineNode]
        var id: String { label }
    }

    /// visibleNodes grouped by calendar day, newest day first, with 今天/昨天
    /// relative labels for the ledger's pinned section headers.
    var dayGroups: [DayGroup] {
        let calendar = Calendar.current
        var groups: [(day: Date, nodes: [TimelineNode])] = []
        for node in visibleNodes {
            let day = calendar.startOfDay(for: node.command.timestamp)
            if let last = groups.indices.last, groups[last].day == day {
                groups[last].nodes.append(node)
            } else {
                groups.append((day, [node]))
            }
        }
        let formatter = DateFormatter()
        formatter.dateFormat = "MM-dd · EEE"
        return groups.map { group in
            let label: String
            if calendar.isDateInToday(group.day) {
                label = Strings.f("timeline.todayWithCount", group.nodes.count)
            } else if calendar.isDateInYesterday(group.day) {
                label = Strings.s("timeline.yesterday")
            } else {
                label = formatter.string(from: group.day)
            }
            return DayGroup(label: label, nodes: group.nodes)
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
        kindFilter = nil
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
