import AppKit
import SwiftUI

private let tokens = DesignTokens.shared

struct TimelineView: View {
    @Bindable var viewModel: TimelineViewModel
    let onTogglePin: () -> Void
    let onOpenSettings: () -> Void
    let onHide: () -> Void

    @AppStorage(SettingsKey.alwaysOnTop) private var alwaysOnTop = true

    var body: some View {
        VStack(spacing: 0) {
            header
                .padding(.horizontal, tokens.spacing.panelPadding)
                .padding(.top, tokens.spacing.panelPadding)
                .padding(.bottom, 8)
            Divider().opacity(0.4)
            timeline
        }
    }

    private var header: some View {
        HStack(spacing: 8) {
            Image(systemName: "clock.arrow.trianglehead.counterclockwise.rotate.90")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(tokens.color.accent.color)
            Text("Agent Timeline")
                .font(.system(size: tokens.typography.size.title, weight: .semibold))
                .foregroundStyle(tokens.color.textPrimary.color)
            Spacer()
            projectMenu
            kindMenu
            dictionaryButton
            Button {
                alwaysOnTop.toggle()
                onTogglePin()
            } label: {
                Image(systemName: alwaysOnTop ? "pin.fill" : "pin")
                    .font(.system(size: 11))
                    .foregroundStyle(alwaysOnTop ? tokens.color.accent.color : tokens.color.textTertiary.color)
            }
            .buttonStyle(.plain)
            .help(alwaysOnTop ? "取消置顶" : "窗口置顶")
            Button(action: onOpenSettings) {
                Image(systemName: "gearshape")
                    .font(.system(size: 11))
                    .foregroundStyle(tokens.color.textTertiary.color)
            }
            .buttonStyle(.plain)
            .help("设置")
            Button(action: onHide) {
                Image(systemName: "xmark")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(tokens.color.textTertiary.color)
            }
            .buttonStyle(.plain)
            .help("隐藏面板（menu bar 图标可重新打开）")
        }
    }

    private var projectMenu: some View {
        Menu {
            Button("全部项目") { viewModel.projectFilter = nil }
            Divider()
            ForEach(viewModel.projects, id: \.self) { project in
                Button {
                    viewModel.projectFilter = project
                } label: {
                    if viewModel.projectFilter == project {
                        Label(project, systemImage: "checkmark")
                    } else {
                        Text(project)
                    }
                }
            }
        } label: {
            HStack(spacing: 3) {
                Image(systemName: "folder")
                    .font(.system(size: 10))
                Text(viewModel.projectFilter ?? "全部")
                    .font(.system(size: tokens.typography.size.caption))
                    .lineLimit(1)
            }
            .foregroundStyle(tokens.color.textSecondary.color)
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
    }

    private var kindMenu: some View {
        Menu {
            Button("全部阶段") { viewModel.kindFilter = nil }
            Divider()
            ForEach(NodeKind.allCases, id: \.rawValue) { kind in
                Button {
                    viewModel.kindFilter = kind.rawValue
                } label: {
                    if viewModel.kindFilter == kind.rawValue {
                        Label(kind.rawValue, systemImage: "checkmark")
                    } else {
                        Text(kind.rawValue)
                    }
                }
            }
        } label: {
            HStack(spacing: 3) {
                Image(systemName: "square.stack.3d.up")
                    .font(.system(size: 10))
                Text(viewModel.kindFilter ?? "阶段")
                    .font(.system(size: tokens.typography.size.caption))
            }
            .foregroundStyle(viewModel.kindFilter == nil
                             ? tokens.color.textSecondary.color : tokens.color.accent.color)
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
    }

    @State private var showDictionary = false

    private var dictionaryButton: some View {
        Button {
            showDictionary.toggle()
        } label: {
            Image(systemName: "character.book.closed")
                .font(.system(size: 11))
                .foregroundStyle(tokens.color.textTertiary.color)
        }
        .buttonStyle(.plain)
        .help("代号词典")
        .popover(isPresented: $showDictionary, arrowEdge: .bottom) {
            CodenameDictionaryView(viewModel: viewModel) { showDictionary = false }
                .onAppear {
                    NotificationCenter.default.post(
                        name: FloatingPanel.holdReadableNotification, object: nil,
                        userInfo: ["hold": true])
                }
                .onDisappear {
                    NotificationCenter.default.post(
                        name: FloatingPanel.holdReadableNotification, object: nil,
                        userInfo: ["hold": false])
                }
        }
    }

    private var timeline: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 0, pinnedViews: [.sectionHeaders]) {
                    if viewModel.visibleNodes.isEmpty {
                        emptyState
                    }
                    ForEach(viewModel.dayGroups) { group in
                        Section {
                            ForEach(group.nodes) { node in
                                NodeCardView(node: node, viewModel: viewModel)
                                    .id(node.id)
                            }
                        } header: {
                            DayHeader(label: group.label)
                        }
                    }
                    if viewModel.canLoadMore {
                        Button("加载更早…") { viewModel.loadMore() }
                            .buttonStyle(.plain)
                            .font(.system(size: tokens.typography.size.caption))
                            .foregroundStyle(tokens.color.accent.color)
                            .padding(.vertical, 8)
                    }
                }
                .padding(.leading, tokens.spacing.panelPadding)
            }
            .onChange(of: viewModel.scrollTarget) { _, target in
                guard let target else { return }
                withAnimation(.easeInOut(duration: 0.25)) {
                    proxy.scrollTo(target, anchor: .center)
                }
                viewModel.scrollTarget = nil
            }
        }
    }

    /// Pinned day divider: opaque-ish backing so it never ghosts over entries on
    /// the blur; a short tick crosses the rail at its vertical center.
    private struct DayHeader: View {
        let label: String

        var body: some View {
            HStack(spacing: 8) {
                Rectangle()
                    .fill(tokens.color.dayHeaderRule.color)
                    .frame(width: 6, height: 2)
                    .padding(.leading, (tokens.spacing.railGutter - 6) / 2)
                Text(label)
                    .font(.system(size: tokens.typography.size.dayHeader, weight: .medium))
                    .kerning(tokens.typography.letterSpacing.dayHeader)
                    .foregroundStyle(tokens.color.dayHeaderText.color)
                    .lineLimit(1)
                    .fixedSize()
                Rectangle()
                    .fill(tokens.color.dayHeaderRule.color)
                    .frame(height: 1)
            }
            .padding(.vertical, 5)
            .padding(.trailing, tokens.spacing.panelPadding)
            .background(tokens.color.dayHeaderBg.color)
        }
    }

    private var emptyState: some View {
        VStack(spacing: 8) {
            Image(systemName: "moon.zzz")
                .font(.system(size: 24))
                .foregroundStyle(tokens.color.textTertiary.color)
            Text("暂无记录 — 在 Claude Code / Codex / Kimi 中提交命令后，这里会自动出现时间线")
                .font(.system(size: tokens.typography.size.caption))
                .foregroundStyle(tokens.color.textTertiary.color)
                .multilineTextAlignment(.center)
        }
        .padding(.top, 60)
        .padding(.horizontal, 20)
    }
}
