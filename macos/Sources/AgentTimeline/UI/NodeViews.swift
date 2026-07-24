import AppKit
import SwiftUI

private let tokens = DesignTokens.shared

struct NodeCardView: View {
    let node: TimelineNode
    @Bindable var viewModel: TimelineViewModel

    private var isExpanded: Bool { viewModel.expanded.contains(node.id) }

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "MM-dd HH:mm"
        return f
    }()

    var body: some View {
        HStack(alignment: .top, spacing: tokens.spacing.cardGap) {
            Circle()
                .fill(tokens.color.badgeColor(node.command.agent))
                .frame(width: tokens.spacing.railDotSize, height: tokens.spacing.railDotSize)
                .padding(.top, 4)

            VStack(alignment: .leading, spacing: 5) {
                header
                Text(title)
                    .font(.system(size: tokens.typography.size.title, weight: .semibold))
                    .foregroundStyle(tokens.color.textPrimary.color)
                    .textSelection(.enabled)
                    .lineLimit(isExpanded ? nil : 2)
                keyPoints
                codenameChips
                if let result = node.summary?.resultLine, !result.isEmpty {
                    Text("→ " + result)
                        .font(.system(size: tokens.typography.size.caption))
                        .foregroundStyle(tokens.color.resultLine.color)
                        .textSelection(.enabled)
                        .lineLimit(isExpanded ? nil : 1)
                }
                if isExpanded {
                    expandedBody
                }
            }
        }
        .padding(tokens.spacing.cardPadding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: tokens.radius.card)
                .fill(tokens.color.cardBackground.color)
                .overlay(
                    RoundedRectangle(cornerRadius: tokens.radius.card)
                        .strokeBorder(tokens.color.cardBorder.color, lineWidth: 1)))
        .contentShape(Rectangle())
    }

    private var title: String {
        node.summary?.title ?? ParserSupport.truncate(node.command.text, to: 40)
    }

    private var header: some View {
        HStack(spacing: 6) {
            Text(Self.timeFormatter.string(from: node.command.timestamp))
                .font(.system(size: tokens.typography.size.caption).monospacedDigit())
                .foregroundStyle(tokens.color.textTertiary.color)
            Text(node.command.project)
                .font(.system(size: tokens.typography.size.caption))
                .foregroundStyle(tokens.color.textSecondary.color)
                .lineLimit(1)
            Text(node.command.agent.displayName)
                .font(.system(size: tokens.typography.size.chip, weight: .medium))
                .foregroundStyle(tokens.color.badgeColor(node.command.agent))
            Spacer(minLength: 0)
            Button {
                if isExpanded {
                    viewModel.expanded.remove(node.id)
                } else {
                    viewModel.expanded.insert(node.id)
                }
            } label: {
                Image(systemName: isExpanded ? "chevron.up" : "chevron.down")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(tokens.color.textTertiary.color)
            }
            .buttonStyle(.plain)
        }
    }

    @ViewBuilder
    private var keyPoints: some View {
        let points = node.summary?.keyPoints ?? []
        if !points.isEmpty {
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

    private var expandedBody: some View {
        VStack(alignment: .leading, spacing: 6) {
            Divider()
            HStack {
                Text("原始命令")
                    .font(.system(size: tokens.typography.size.chip, weight: .medium))
                    .foregroundStyle(tokens.color.textTertiary.color)
                Spacer()
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(node.command.text, forType: .string)
                } label: {
                    Image(systemName: "doc.on.doc")
                        .font(.system(size: 9))
                        .foregroundStyle(tokens.color.textTertiary.color)
                }
                .buttonStyle(.plain)
                .help("复制原始命令")
            }
            Text(node.command.text)
                .font(.system(size: tokens.typography.size.caption))
                .foregroundStyle(tokens.color.textPrimary.color)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

struct CodenameChip: View {
    let name: String
    @Bindable var viewModel: TimelineViewModel
    @State private var showPopover = false

    var body: some View {
        Button {
            showPopover.toggle()
        } label: {
            Text(name)
                .font(.system(size: tokens.typography.size.chip, weight: .medium).monospaced())
                .foregroundStyle(tokens.color.codenameChipText.color)
                .padding(.horizontal, tokens.spacing.chipPaddingH)
                .padding(.vertical, tokens.spacing.chipPaddingV)
                .background(
                    RoundedRectangle(cornerRadius: tokens.radius.chip)
                        .fill(tokens.color.codenameChipBg.color))
        }
        .buttonStyle(.plain)
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
            Text(name)
                .font(.system(size: tokens.typography.size.title, weight: .semibold).monospaced())
            if let entry = viewModel.entry(forCodename: name) {
                if !entry.definition.isEmpty {
                    Text(entry.definition)
                        .font(.system(size: tokens.typography.size.body))
                        .textSelection(.enabled)
                } else {
                    Text("暂无定义（等待摘要提炼）")
                        .font(.system(size: tokens.typography.size.caption))
                        .foregroundStyle(.secondary)
                }
                Text("首次出现 \(entry.firstSeen.formatted(date: .abbreviated, time: .shortened)) · 共 \(entry.occurrences) 次")
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
        .frame(minWidth: 200, maxWidth: 320)
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
