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

    private var timeline: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: tokens.spacing.cardGap) {
                    if viewModel.visibleNodes.isEmpty {
                        emptyState
                    }
                    ForEach(viewModel.visibleNodes) { node in
                        NodeCardView(node: node, viewModel: viewModel)
                            .id(node.id)
                    }
                }
                .padding(tokens.spacing.panelPadding)
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
