import AppKit
import SwiftUI

private let tokens = DesignTokens.shared

/// Ledger entry (boxless). The visual grammar the user learns once:
///   ❯ + solid agent-colored rule + opaque paper block = my literal words
///   ✦ + dotted gray rule                              = machine-derived
/// Rail markers carry kind/importance; a ring marks codename-defining nodes.
struct NodeCardView: View {
    let node: TimelineNode
    @Bindable var viewModel: TimelineViewModel

    @State private var hovering = false
    @State private var copied = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var isExpanded: Bool { viewModel.expanded.contains(node.id) }
    private var agentColor: Color { tokens.color.badgeColor(node.command.agent) }
    private var kindRaw: String? { node.summary?.kind }

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "MM-dd HH:mm"
        return f
    }()

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            railGutter
            content
                .padding(.vertical, tokens.spacing.entryPaddingV)
                .padding(.trailing, tokens.spacing.panelPadding)
        }
        .background(anchorWash)
        .background(hovering ? tokens.color.entryHover.color : .clear)
        .contentShape(Rectangle())
        .onTapGesture { toggleExpanded() }
        .onHover { inside in
            withAnimation(.easeOut(duration: tokens.opacity.hoverFadeMs / 1000)) {
                hovering = inside
            }
        }
        .contextMenu { menuItems }
        .overlay(alignment: .bottom) {
            Rectangle()
                .fill(tokens.color.entryDivider.color)
                .frame(height: 1)
                .padding(.leading, tokens.spacing.railGutter)
        }
    }

    // MARK: - Rail

    /// Continuous 2px rail per entry (segments join visually) + kind marker
    /// aligned to the command block's first line, ringed on definition nodes.
    private var railGutter: some View {
        ZStack(alignment: .top) {
            Rectangle()
                .fill(tokens.color.timelineRail.color)
                .frame(width: tokens.spacing.railWidth)
                .frame(maxHeight: .infinity)
            marker
                .padding(.top, tokens.spacing.entryPaddingV + 24)
        }
        .frame(width: tokens.spacing.railGutter)
    }

    @ViewBuilder
    private var marker: some View {
        let isDefinition = viewModel.definitionNodeIds.contains(node.id)
        let kindColor = tokens.color.kindColor(kindRaw) ?? tokens.color.timelineRail.color
        Group {
            switch kindRaw {
            case NodeKind.requirement.rawValue, NodeKind.decision.rawValue:
                Rectangle()
                    .fill(kindColor)
                    .frame(width: tokens.marker.anchor / 1.35, height: tokens.marker.anchor / 1.35)
                    .rotationEffect(.degrees(45))
            case NodeKind.task.rawValue, NodeKind.fix.rawValue,
                 NodeKind.research.rawValue, NodeKind.learning.rawValue:
                Circle()
                    .fill(kindColor)
                    .frame(width: tokens.marker.standard, height: tokens.marker.standard)
            default:
                Circle()
                    .strokeBorder(tokens.color.timelineRail.color, lineWidth: 1)
                    .background(Circle().fill(tokens.color.dayHeaderBg.color))
                    .frame(width: tokens.marker.minor, height: tokens.marker.minor)
            }
        }
        .overlay {
            if isDefinition {
                Circle()
                    .strokeBorder(tokens.color.accent.color, lineWidth: tokens.marker.definitionRingWidth)
                    .frame(width: tokens.marker.anchor + 2 * tokens.marker.definitionRingOffset,
                           height: tokens.marker.anchor + 2 * tokens.marker.definitionRingOffset)
            }
        }
    }

    // MARK: - Content

    private var content: some View {
        VStack(alignment: .leading, spacing: 5) {
            metaRow
            commandBlock
            derivedBlock
        }
    }

    private var metaRow: some View {
        HStack(spacing: 6) {
            Text(Self.timeFormatter.string(from: node.command.timestamp))
                .font(.system(size: tokens.typography.size.caption).monospacedDigit())
                .foregroundStyle(tokens.color.textSecondary.color)
            Text(node.command.project)
                .font(.system(size: tokens.typography.size.caption))
                .foregroundStyle(tokens.color.textSecondary.color)
                .lineLimit(1)
            AgentBadge(agent: node.command.agent)
                .help(node.command.agent.displayName)
            if let kind = kindRaw, let color = tokens.color.kindColor(kind) {
                Text(kind)
                    .font(.system(size: tokens.typography.size.chip, weight: .medium))
                    .foregroundStyle(color)
                    .padding(.horizontal, 4)
                    .padding(.vertical, 1)
                    .background(RoundedRectangle(cornerRadius: 3).fill(color.opacity(0.14)))
            }
            Spacer(minLength: 0)
            Button(action: toggleExpanded) {
                Image(systemName: "chevron.down")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(tokens.color.textTertiary.color)
                    .rotationEffect(.degrees(isExpanded ? 180 : 0))
            }
            .buttonStyle(.plain)
            .frame(width: 24, height: 24)
            .contentShape(Rectangle())
        }
    }

    /// The hero: my literal words on an opaque "paper" block that survives the
    /// panel's translucency, flattened corner pointing at the rail marker.
    private var commandBlock: some View {
        HStack(alignment: .top, spacing: 0) {
            Text(tokens.glyph.prompt)
                .font(.custom("SF Mono", size: tokens.typography.size.command))
                .foregroundStyle(agentColor)
                .frame(width: tokens.spacing.hangIndent, alignment: .leading)
            Text(node.command.text)
                .font(.system(size: tokens.typography.size.command, weight: .semibold))
                .foregroundStyle(tokens.color.commandText.color)
                .lineSpacing(tokens.typography.lineSpacing)
                .lineLimit(isExpanded ? nil : tokens.lineLimit.commandCollapsed)
                .truncationMode(.tail)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, tokens.spacing.commandPaddingH)
        .padding(.vertical, tokens.spacing.commandPaddingV)
        .background(
            UnevenRoundedRectangle(
                topLeadingRadius: tokens.radius.commandBlockAttach,
                bottomLeadingRadius: tokens.radius.commandBlock,
                bottomTrailingRadius: tokens.radius.commandBlock,
                topTrailingRadius: tokens.radius.commandBlock)
                .fill(tokens.color.commandBg.color))
        .overlay(
            // Self-contained edge: the paper never dissolves into a lookalike backdrop.
            UnevenRoundedRectangle(
                topLeadingRadius: tokens.radius.commandBlockAttach,
                bottomLeadingRadius: tokens.radius.commandBlock,
                bottomTrailingRadius: tokens.radius.commandBlock,
                topTrailingRadius: tokens.radius.commandBlock)
                .strokeBorder(tokens.color.surfaceStroke.color, lineWidth: 1))
        .overlay(alignment: .leading) {
            UnevenRoundedRectangle(
                topLeadingRadius: tokens.radius.commandBlockAttach,
                bottomLeadingRadius: tokens.radius.commandBlock,
                bottomTrailingRadius: 0, topTrailingRadius: 0)
                .fill(agentColor)
                .frame(width: tokens.spacing.quoteRuleWidth)
        }
        .overlay(alignment: .topTrailing) {
            if hovering || copied {
                copyButton
                    .padding(4)
                    .transition(.opacity)
            }
        }
    }

    private var copyButton: some View {
        Button {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(node.command.text, forType: .string)
            withAnimation(.easeOut(duration: 0.15)) { copied = true }
            DispatchQueue.main.asyncAfter(deadline: .now() + tokens.motion.copyMorphMs / 1000) {
                withAnimation(.easeOut(duration: 0.15)) { copied = false }
            }
        } label: {
            Image(systemName: copied ? "checkmark" : "doc.on.doc")
                .font(.system(size: 10))
                .foregroundStyle(copied ? tokens.color.resultLine.color : tokens.color.textTertiary.color)
        }
        .buttonStyle(.plain)
        .frame(width: 20, height: 20)
        .contentShape(Rectangle())
        .help("复制原话")
    }

    /// Everything machine-generated, subordinated behind one dotted rule.
    @ViewBuilder
    private var derivedBlock: some View {
        let hasAnyDerived = derivedTitle != nil
            || !(node.summary?.keyPoints.isEmpty ?? true)
            || !chipNames.isEmpty
            || !(node.summary?.resultLine ?? "").isEmpty
        if hasAnyDerived {
            HStack(alignment: .top, spacing: tokens.spacing.ruleTextGap) {
                DottedRule()
                    .frame(width: tokens.spacing.derivedRuleWidth)
                VStack(alignment: .leading, spacing: 3) {
                    derivedContent
                }
            }
            .padding(.horizontal, tokens.spacing.commandPaddingH)
            .padding(.vertical, tokens.spacing.commandPaddingV)
            .background(
                RoundedRectangle(cornerRadius: tokens.radius.commandBlock)
                    .fill(tokens.color.derivedBg.color))
            .overlay(
                RoundedRectangle(cornerRadius: tokens.radius.commandBlock)
                    .strokeBorder(tokens.color.surfaceStroke.color, lineWidth: 1))
            .padding(.leading, tokens.spacing.hangIndent)
        }
    }

    /// The machine-derived rows, hosted on the secondary "paper" so they never
    /// sink into the wallpaper bleeding through the translucent panel.
    @ViewBuilder
    private var derivedContent: some View {
        Group {
                    if let title = derivedTitle {
                        HStack(alignment: .top, spacing: 4) {
                            Text(tokens.glyph.derived)
                                .font(.system(size: tokens.typography.size.chip))
                                .foregroundStyle(tokens.color.textTertiary.color)
                            Text(title)
                                .font(.system(size: tokens.typography.size.derivedTitle))
                                .foregroundStyle(tokens.color.textSecondary.color)
                                .lineLimit(1)
                                .textSelection(.enabled)
                        }
                    }
                    keypointsView
                    codenameChips
                    if let result = node.summary?.resultLine, !result.isEmpty {
                        Text("→ " + result)
                            .font(.system(size: tokens.typography.size.caption))
                            .foregroundStyle(tokens.color.resultLine.color)
                            .textSelection(.enabled)
                            .lineLimit(isExpanded ? nil : 1)
                    }
        }
    }

    /// LLM title, suppressed when it merely echoes a short command.
    private var derivedTitle: String? {
        guard let title = node.summary?.title, !title.isEmpty else { return nil }
        let command = node.command.text
        if command.count <= 20 { return nil }
        let normalize: (String) -> String = { s in
            String(s.unicodeScalars.filter { !$0.properties.isWhitespace
                && !CharacterSet.punctuationCharacters.contains($0) })
        }
        let normTitle = normalize(title)
        let normCommand = normalize(command)
        if !normTitle.isEmpty, normCommand.hasPrefix(normTitle) { return nil }
        return title
    }

    @ViewBuilder
    private var keypointsView: some View {
        let points = node.summary?.keyPoints ?? []
        if !points.isEmpty {
            if isExpanded {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(Array(points.enumerated()), id: \.offset) { _, point in
                        HStack(alignment: .top, spacing: 4) {
                            Text("·")
                                .font(.system(size: tokens.typography.size.caption, weight: .bold))
                                .foregroundStyle(tokens.color.accent.color)
                            Text(point)
                                .font(.system(size: tokens.typography.size.caption))
                                .foregroundStyle(tokens.color.textSecondary.color)
                                .textSelection(.enabled)
                        }
                    }
                }
            } else {
                HStack(spacing: 4) {
                    Text(points.joined(separator: " · "))
                        .font(.system(size: tokens.typography.size.caption))
                        .foregroundStyle(tokens.color.textSecondary.color)
                        .lineLimit(tokens.lineLimit.keypointsCollapsed)
                    if points.count > 2 {
                        Text("+\(points.count - 2)")
                            .font(.system(size: tokens.typography.size.chip, weight: .medium))
                            .foregroundStyle(tokens.color.accent.color)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var codenameChips: some View {
        let names = chipNames
        if !names.isEmpty {
            FlowLayout(spacing: 4) {
                ForEach(names, id: \.self) { name in
                    CodenameChip(name: name, viewModel: viewModel)
                }
            }
        }
    }

    private var chipNames: [String] {
        var seen = Set<String>()
        let fromSummary = (node.summary?.codenames ?? []).map(\.name)
        let detected = CodenameDetector.detect(in: node.command.text)
        return (fromSummary + detected).filter { seen.insert($0).inserted }
    }

    /// 需求/决策 anchors get a faint kind-colored wash across the whole entry.
    @ViewBuilder
    private var anchorWash: some View {
        if kindRaw == NodeKind.requirement.rawValue || kindRaw == NodeKind.decision.rawValue,
           let color = tokens.color.kindColor(kindRaw) {
            RoundedRectangle(cornerRadius: tokens.radius.anchorWash)
                .fill(color.opacity(tokens.opacity.anchorWash))
        }
    }

    @ViewBuilder
    private var menuItems: some View {
        Button("复制原话") {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(node.command.text, forType: .string)
        }
        Button("复制摘要") {
            var parts: [String] = []
            if let s = node.summary {
                parts.append(s.title)
                parts.append(contentsOf: s.keyPoints)
                if let r = s.resultLine, !r.isEmpty { parts.append("→ " + r) }
            }
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(parts.joined(separator: "\n"), forType: .string)
        }
        if let first = chipNames.first {
            Button("跳转到 \(first) 定义节点") { viewModel.jumpToDefinition(of: first) }
        }
        Button("只看此项目") { viewModel.projectFilter = node.command.project }
    }

    private func toggleExpanded() {
        let spring = Animation.spring(
            response: tokens.motion.expandSpringResponse,
            dampingFraction: tokens.motion.expandSpringDamping)
        withAnimation(reduceMotion ? nil : spring) {
            if isExpanded {
                viewModel.expanded.remove(node.id)
            } else {
                viewModel.expanded.insert(node.id)
            }
        }
    }
}

/// 16px rounded source badge (CL/CO/KI/ZC on agent color) — shared visual with
/// the Windows entry meta row and project dropdown.
struct AgentBadge: View {
    let agent: AgentKind

    var body: some View {
        Text(agent.monogram)
            .font(.system(size: 7.5, weight: .bold).monospaced())
            .foregroundStyle(.white)
            .frame(width: 16, height: 16)
            .background(
                RoundedRectangle(cornerRadius: 4)
                    .fill(tokens.color.badgeColor(agent)))
    }
}

/// Rasterized badge for AppKit menu items (SwiftUI menus flatten custom views;
/// an explicit NSImage survives).
@MainActor
enum AgentBadgeImage {
    private static var cache: [AgentKind: NSImage] = [:]

    static func image(for agent: AgentKind) -> NSImage {
        if let cached = cache[agent] { return cached }
        let renderer = ImageRenderer(content: AgentBadge(agent: agent).padding(1))
        renderer.scale = 2
        let image = renderer.nsImage ?? NSImage(size: NSSize(width: 16, height: 16))
        image.isTemplate = false
        cache[agent] = image
        return image
    }
}

/// 1px vertical dotted rule — the "machine-derived" ink.
private struct DottedRule: View {
    var body: some View {
        GeometryReader { geo in
            Path { path in
                path.move(to: .zero)
                path.addLine(to: CGPoint(x: 0, y: geo.size.height))
            }
            .stroke(
                tokens.color.derivedRule.color,
                style: StrokeStyle(lineWidth: tokens.spacing.derivedRuleWidth, dash: [2, 3]))
        }
        .frame(width: tokens.spacing.derivedRuleWidth)
    }
}

struct CodenameChip: View {
    let name: String
    @Bindable var viewModel: TimelineViewModel
    @State private var showPopover = false

    private var entry: CodenameEntry? { viewModel.entry(forCodename: name) }

    private var statusBadge: (symbol: String, color: Color)? {
        switch entry?.statusValue {
        case .completed: return ("✓", tokens.color.resultLine.color)
        case .changed: return ("△", tokens.color.statusChanged.color)
        case .active: return ("▶", tokens.color.accent.color)
        default: return nil
        }
    }

    var body: some View {
        Button {
            showPopover.toggle()
        } label: {
            HStack(spacing: 2) {
                Text(name)
                    .font(.system(size: tokens.typography.size.chip, weight: .medium).monospaced())
                    .foregroundStyle(tokens.color.codenameChipText.color)
                if let badge = statusBadge {
                    Text(badge.symbol)
                        .font(.system(size: tokens.typography.size.chip, weight: .bold))
                        .foregroundStyle(badge.color)
                }
            }
            .padding(.horizontal, tokens.spacing.chipPaddingH)
            .padding(.vertical, tokens.spacing.chipPaddingV)
            .background(
                RoundedRectangle(cornerRadius: tokens.radius.chip)
                    .fill(tokens.color.codenameChipBg.color))
        }
        .buttonStyle(.plain)
        .contentShape(Rectangle().inset(by: -tokens.spacing.chipHitInflate))
        .popover(isPresented: $showPopover, arrowEdge: .bottom) {
            popoverContent
                .onAppear { postHold(true) }
                .onDisappear { postHold(false) }
        }
    }

    /// Keep the panel readable while the popover is open — the mouse is inside
    /// the popover's own window, so the panel's tracking area reports "exited".
    private func postHold(_ hold: Bool) {
        NotificationCenter.default.post(
            name: FloatingPanel.holdReadableNotification, object: nil,
            userInfo: ["hold": hold])
    }

    @ViewBuilder
    private var popoverContent: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Text(name)
                    .font(.system(size: tokens.typography.size.title, weight: .semibold).monospaced())
                if let entry, !entry.status.isEmpty {
                    Text(entry.status)
                        .font(.system(size: tokens.typography.size.chip, weight: .medium))
                        .foregroundStyle(statusBadge?.color ?? tokens.color.textSecondary.color)
                        .padding(.horizontal, 4)
                        .padding(.vertical, 1)
                        .background(RoundedRectangle(cornerRadius: 3)
                            .fill((statusBadge?.color ?? tokens.color.textSecondary.color).opacity(0.14)))
                }
            }
            if let entry {
                if !entry.definition.isEmpty {
                    Text(entry.definition)
                        .font(.system(size: tokens.typography.size.body))
                        .textSelection(.enabled)
                } else {
                    Text("暂无定义（等待摘要提炼或定义式重述）")
                        .font(.system(size: tokens.typography.size.caption))
                        .foregroundStyle(.secondary)
                }
                if !entry.lastContext.isEmpty {
                    Text("最近提及：…\(entry.lastContext)…")
                        .font(.system(size: tokens.typography.size.caption))
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                }
                Text(metaLine(entry))
                    .font(.system(size: tokens.typography.size.caption))
                    .foregroundStyle(.secondary)
                Button("跳转到定义节点") {
                    showPopover = false
                    viewModel.jumpToDefinition(of: name)
                }
                .font(.system(size: tokens.typography.size.caption))
            } else {
                Text("尚未登记")
                    .font(.system(size: tokens.typography.size.caption))
                    .foregroundStyle(.secondary)
            }
        }
        .padding(10)
        .frame(minWidth: 220, maxWidth: 340)
    }

    private func metaLine(_ entry: CodenameEntry) -> String {
        var line = "首次 \(entry.firstSeen.formatted(date: .abbreviated, time: .shortened)) · 共 \(entry.occurrences) 次"
        if let updated = entry.updated {
            line += " · 更新 \(updated.formatted(date: .abbreviated, time: .shortened))"
        }
        return line
    }
}

/// Minimal wrapping layout for codename chips.
struct FlowLayout: Layout {
    var spacing: CGFloat = 4

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let maxWidth = proposal.width ?? 320
        var x: CGFloat = 0, y: CGFloat = 0, rowHeight: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x > 0, x + size.width > maxWidth {
                x = 0
                y += rowHeight + spacing
                rowHeight = 0
            }
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
        return CGSize(width: maxWidth, height: y + rowHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, rowHeight: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x > bounds.minX, x + size.width > bounds.maxX {
                x = bounds.minX
                y += rowHeight + spacing
                rowHeight = 0
            }
            subview.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
    }
}
