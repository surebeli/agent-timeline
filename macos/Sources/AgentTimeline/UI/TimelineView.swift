import AppKit
import SwiftUI

private let tokens = DesignTokens.shared

struct TimelineView: View {
    /// 语言切换后重算 body——Strings.s(...) 是普通函数调用，SwiftUI 不会自己知道表换了。
    @ObservedObject private var languageWatcher = LanguageWatcher.shared
    @AppStorage(SettingsKey.panelCollapsed) private var collapsed = false

    @Bindable var viewModel: TimelineViewModel
    let onTogglePin: () -> Void
    let onToggleCollapse: () -> Void
    let onOpenSettings: () -> Void

    @AppStorage(SettingsKey.alwaysOnTop) private var alwaysOnTop = true

    var body: some View {
        VStack(spacing: 0) {
            header
                .padding(.horizontal, tokens.spacing.panelPadding)
                .padding(.bottom, 8)
                // 头部整体高度对齐系统标题栏（28pt），内容垂直居中 →
                // 标题与右侧控件正好落在交通灯那一行上。
                .frame(height: 28, alignment: .center)
                .padding(.top, 4)
            if !collapsed {
                Divider().opacity(0.4)
                timeline
            }
        }
        // Scrim between the blur and the content: still translucent, but it
        // compresses whatever bleeds through (dark IDEs, bright pages) into a
        // predictable base so the token palette always has its contrast.
        .background(tokens.color.panelScrim.color)
    }

    /// 原生交通灯占位：按钮由系统绘制在标题栏左上角（x≈7，直径 14），
    /// 头部内容让出这段宽度，避免压在按钮上。
    private static let trafficLightInset: CGFloat = 26

    private var header: some View {
        HStack(spacing: 8) {
            Text("Agent Timeline")
                .font(.system(size: tokens.typography.size.title, weight: .semibold))
                .foregroundStyle(tokens.color.textPrimary.color)
                .padding(.leading, Self.trafficLightInset)
            Spacer()
            projectMenu
            kindMenu
            dictionaryButton
            collapseButton
            pinButton
            settingsButton
        }
    }

    private var projectMenu: some View {
        Menu {
            Button(Strings.s("filter.allProjectsItem")) { viewModel.projectFilter = nil }
            Divider()
            ForEach(viewModel.projects, id: \.self) { project in
                Button {
                    viewModel.projectFilter = project
                } label: {
                    // Source badge follows the project's most recently active agent
                    // (win-parity); selection shown as a text check prefix since
                    // the icon slot carries the badge.
                    if let agent = viewModel.projectRecentAgents[project] {
                        Label {
                            Text((viewModel.projectFilter == project ? "✓ " : "") + project)
                        } icon: {
                            Image(nsImage: AgentBadgeImage.image(for: agent))
                        }
                    } else if viewModel.projectFilter == project {
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
                Text(UiText.projectOption(viewModel.projectFilter ?? UiText.allProjects, compact: true))
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
            Button(Strings.s("filter.allKindsItem")) { viewModel.kindFilter = nil }
            Divider()
            ForEach(NodeKind.allCases, id: \.rawValue) { kind in
                Button {
                    viewModel.kindFilter = kind.rawValue
                } label: {
                    if viewModel.kindFilter == kind.rawValue {
                        Label(UiText.kind(kind.rawValue), systemImage: "checkmark")
                    } else {
                        Text(UiText.kind(kind.rawValue))
                    }
                }
            }
        } label: {
            HStack(spacing: 3) {
                Image(systemName: "square.stack.3d.up")
                    .font(.system(size: 10))
                Text(UiText.kindOption(viewModel.kindFilter ?? UiText.allKinds, compact: true))
                    .font(.system(size: tokens.typography.size.caption))
            }
            .foregroundStyle(viewModel.kindFilter == nil
                             ? tokens.color.textSecondary.color : tokens.color.accent.color)
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
    }

    @State private var showDictionary = false

    /// 图标按钮的命中框。字形只有 11pt，直接当按钮几乎点不到——实测九点探测里
    /// 只有正中一点命中，偏 8pt 就全落空。
    ///
    /// 21×26，**沿用头部原有的 8pt 间距**：相邻中心距变成 29pt > 21pt 命中框宽，
    /// 命中区绝不重叠（不会「想点置顶结果折叠了」）。控件组因此宽了约 32pt，
    /// 由 Spacer 吸收，齿轮到窗口边的距离仍≈基线。
    ///
    /// 试过两条更「省地方」的路，都不行，别再走：
    /// · 组内间距置 0 + 21pt 命中框 → 相邻中心距正好 21pt，命中区首尾相接，
    ///   且整行被撑得挤压、齿轮贴边；靠尾部补一个经验值又把图标整体左移了 17pt；
    /// · `.padding(x).contentShape(...).padding(-x)` 反向抵消（Windows 侧 chip 的手法）
    ///   → SwiftUI 里负 padding 把布局缩回去后命中测试也跟着缩，九点探测仍只有正中命中。
    ///
    /// ⚠️ **不要改用 `.padding(x).contentShape(...).padding(-x)` 反向抵消**：
    /// 那是 Windows 侧 chip 用的手法，但在 SwiftUI 里负 padding 把布局尺寸缩回去后
    /// 命中测试也跟着缩，实测九点探测仍然只有正中命中（等于没放大）。
    private static let iconHit = CGSize(width: 21, height: 26)


    private var collapseButton: some View {
        Button(action: onToggleCollapse) {
            // 折叠态给「展开」箭头，展开态给「折叠」箭头——图标指向操作后的方向
            Image(systemName: collapsed ? "chevron.down" : "chevron.up")
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(tokens.color.textTertiary.color)
                .frame(width: Self.iconHit.width, height: Self.iconHit.height)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(Strings.s(collapsed ? "header.expand" : "header.collapse"))
    }

    private var pinButton: some View {
        Button {
            alwaysOnTop.toggle()
            onTogglePin()
        } label: {
            Image(systemName: alwaysOnTop ? "pin.fill" : "pin")
                .font(.system(size: 11))
                .foregroundStyle(alwaysOnTop ? tokens.color.accent.color : tokens.color.textTertiary.color)
                .frame(width: Self.iconHit.width, height: Self.iconHit.height)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(alwaysOnTop ? Strings.s("timeline.unpin") : Strings.s("settings.alwaysOnTop"))
    }

    private var settingsButton: some View {
        Button(action: onOpenSettings) {
            Image(systemName: "gearshape")
                .font(.system(size: 11))
                .foregroundStyle(tokens.color.textTertiary.color)
                .frame(width: Self.iconHit.width, height: Self.iconHit.height)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(Strings.s("header.settings"))
    }

    private var dictionaryButton: some View {
        Button {
            showDictionary.toggle()
        } label: {
            Image(systemName: "character.book.closed")
                .font(.system(size: 11))
                .foregroundStyle(tokens.color.textTertiary.color)
                .frame(width: Self.iconHit.width, height: Self.iconHit.height)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(Strings.s("header.dictionary"))
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
                        Button(Strings.s("timeline.loadMore")) { viewModel.loadMore() }
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
            Text(Strings.s("timeline.empty"))
                .font(.system(size: tokens.typography.size.caption))
                .foregroundStyle(tokens.color.textTertiary.color)
                .multilineTextAlignment(.center)
        }
        .padding(.top, 60)
        .padding(.horizontal, 20)
    }
}
