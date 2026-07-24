import AppKit
import SwiftUI

/// Typed view over design/design-tokens.json — the shared mac/win visual spec.
/// The file is bundled as a resource; values here must stay in sync with the
/// Windows renderer, so never hardcode colors/sizes in views.
struct DesignTokens: Codable {
    struct DualColor: Codable {
        let light: String
        let dark: String

        var color: Color {
            Color(nsColor: NSColor(name: nil) { appearance in
                let isDark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
                return NSColor(hexString: isDark ? dark : light) ?? .labelColor
            })
        }
    }

    struct Colors: Codable {
        let accent: DualColor
        let textPrimary: DualColor
        let textSecondary: DualColor
        let textTertiary: DualColor
        let cardBackground: DualColor
        let cardBorder: DualColor
        let timelineRail: DualColor
        let codenameChipBg: DualColor
        let codenameChipText: DualColor
        let resultLine: DualColor
        let agentBadge: [String: String]

        func badgeColor(_ agent: AgentKind) -> Color {
            Color(nsColor: NSColor(hexString: agentBadge[agent.rawValue] ?? "#888888") ?? .systemGray)
        }
    }

    struct Typography: Codable {
        struct Sizes: Codable {
            let title: Double
            let body: Double
            let caption: Double
            let chip: Double
        }
        let size: Sizes
        let lineSpacing: Double
    }

    struct Spacing: Codable {
        let panelPadding: Double
        let cardPadding: Double
        let cardGap: Double
        let railWidth: Double
        let railDotSize: Double
        let chipPaddingH: Double
        let chipPaddingV: Double
    }

    struct Radius: Codable {
        let panel: Double
        let card: Double
        let chip: Double
    }

    struct Opacity: Codable {
        let hover: Double
        let idle: Double
        let transitionMs: Double
    }

    struct Panel: Codable {
        let defaultWidth: Double
        let minWidth: Double
        let maxWidth: Double
        let defaultHeight: Double
    }

    let color: Colors
    let typography: Typography
    let spacing: Spacing
    let radius: Radius
    let opacity: Opacity
    let panel: Panel

    static let shared: DesignTokens = {
        // Tokens are embedded at build time (DesignTokensData.swift, generated from
        // design/design-tokens.json) — no resource bundle, no runtime file I/O.
        guard let data = DesignTokensData.json.data(using: .utf8),
              let tokens = try? JSONDecoder().decode(DesignTokens.self, from: data)
        else {
            fatalError("embedded design tokens malformed — regenerate DesignTokensData.swift")
        }
        return tokens
    }()
}

extension NSColor {
    /// #RGB, #RRGGBB or #RRGGBBAA.
    convenience init?(hexString: String) {
        var hex = hexString.trimmingCharacters(in: .whitespaces)
        if hex.hasPrefix("#") { hex.removeFirst() }
        if hex.count == 3 {
            hex = hex.map { "\($0)\($0)" }.joined()
        }
        guard hex.count == 6 || hex.count == 8,
              let value = UInt64(hex, radix: 16) else { return nil }
        let hasAlpha = hex.count == 8
        let r, g, b, a: CGFloat
        if hasAlpha {
            r = CGFloat((value >> 24) & 0xFF) / 255
            g = CGFloat((value >> 16) & 0xFF) / 255
            b = CGFloat((value >> 8) & 0xFF) / 255
            a = CGFloat(value & 0xFF) / 255
        } else {
            r = CGFloat((value >> 16) & 0xFF) / 255
            g = CGFloat((value >> 8) & 0xFF) / 255
            b = CGFloat(value & 0xFF) / 255
            a = 1
        }
        self.init(srgbRed: r, green: g, blue: b, alpha: a)
    }
}
