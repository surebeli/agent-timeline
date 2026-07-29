import SwiftUI

private let tokens = DesignTokens.shared

/// The recall surface for batch codenames (N1/T2/SG-5…): every registered code
/// with its current status, definition and last-mention context, most recently
/// updated first. Click → jump to the defining node on the timeline.
struct CodenameDictionaryView: View {
    /// 语言切换后重算 body——Strings.s(...) 是普通函数调用，SwiftUI 不会自己知道表换了。
    @ObservedObject private var languageWatcher = LanguageWatcher.shared

    @Bindable var viewModel: TimelineViewModel
    let onClose: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(Strings.f("dict.title", viewModel.sortedCodenames.count))
                .font(.system(size: tokens.typography.size.title, weight: .semibold))
                .padding(.horizontal, 12)
                .padding(.vertical, 10)
            Divider()
            if viewModel.sortedCodenames.isEmpty {
                Text(Strings.s("dict.empty"))
                    .font(.system(size: tokens.typography.size.caption))
                    .foregroundStyle(.secondary)
                    .padding(12)
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(viewModel.sortedCodenames) { entry in
                            row(entry)
                            Divider().opacity(0.3)
                        }
                    }
                }
                .frame(maxHeight: 380)
            }
        }
        .frame(width: 340)
    }

    private func row(_ entry: CodenameEntry) -> some View {
        Button {
            onClose()
            viewModel.jumpToDefinition(of: entry.name)
        } label: {
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(entry.name)
                        .font(.system(size: tokens.typography.size.body, weight: .semibold).monospaced())
                        .foregroundStyle(tokens.color.codenameChipText.color)
                    if !entry.status.isEmpty {
                        Text(UiText.status(entry.status))
                            .font(.system(size: tokens.typography.size.chip, weight: .medium))
                            .foregroundStyle(statusColor(entry))
                            .padding(.horizontal, 4)
                            .padding(.vertical, 1)
                            .background(RoundedRectangle(cornerRadius: 3)
                                .fill(statusColor(entry).opacity(0.14)))
                    }
                    Spacer()
                    Text(relativeTime(entry.updated ?? entry.firstSeen))
                        .font(.system(size: tokens.typography.size.chip))
                        .foregroundStyle(tokens.color.textTertiary.color)
                }
                Text(entry.definition.isEmpty ? Strings.s("dict.noDefinition") : entry.definition)
                    .font(.system(size: tokens.typography.size.caption))
                    .foregroundStyle(entry.definition.isEmpty
                                     ? tokens.color.textTertiary.color : tokens.color.textSecondary.color)
                    .lineLimit(2)
                if !entry.lastContext.isEmpty {
                    Text(Strings.f("dict.lastMention", entry.lastContext))
                        .font(.system(size: tokens.typography.size.chip))
                        .foregroundStyle(tokens.color.textTertiary.color)
                        .lineLimit(1)
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private func statusColor(_ entry: CodenameEntry) -> Color {
        switch entry.statusValue {
        case .completed: return tokens.color.resultLine.color
        case .changed: return tokens.color.statusChanged.color
        case .active: return tokens.color.accent.color
        default: return tokens.color.textSecondary.color
        }
    }

    private func relativeTime(_ date: Date) -> String {
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .abbreviated
        return formatter.localizedString(for: date, relativeTo: Date())
    }
}
