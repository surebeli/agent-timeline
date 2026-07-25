#!/usr/bin/env swift
// gen-icon.swift — Agent Timeline app icon generator.
//
// Renders the "ledger timeline" mark (rail + markers + ❯ prompt + paper blocks)
// at every iconset size and packs an .icns.
//
// Usage:  swift macos/scripts/gen-icon.swift <outdir>
// Output: <outdir>/AgentTimeline.iconset/icon_*.png  and  <outdir>/AppIcon.icns
//
// Colors come from design/design-tokens.json (accent #4F6BF0 / #7B93FF,
// deep-indigo background family). Regenerate only when the mark changes;
// the committed macos/Assets/AppIcon.icns is the build input.

import AppKit
import SwiftUI
import CoreText
import UniformTypeIdentifiers

// MARK: - CLI

guard CommandLine.arguments.count >= 2 else {
    FileHandle.standardError.write(Data("usage: swift gen-icon.swift <outdir>\n".utf8))
    exit(64)
}
let outDir = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
let iconset = outDir.appendingPathComponent("AgentTimeline.iconset", isDirectory: true)
let fm = FileManager.default
try? fm.removeItem(at: iconset)
try fm.createDirectory(at: iconset, withIntermediateDirectories: true)

// MARK: - Helpers

let srgb = CGColorSpace(name: CGColorSpace.sRGB)!

func rgba(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(colorSpace: srgb, components: [
        CGFloat((hex >> 16) & 0xFF) / 255,
        CGFloat((hex >> 8) & 0xFF) / 255,
        CGFloat(hex & 0xFF) / 255,
        a,
    ])!
}

/// Apple-style continuous-corner ("squircle") rounded rect.
func squircle(_ rect: CGRect, _ radius: CGFloat) -> CGPath {
    RoundedRectangle(cornerRadius: radius, style: .continuous).path(in: rect).cgPath
}

func diamond(center: CGPoint, half: CGFloat) -> CGPath {
    let p = CGMutablePath()
    p.move(to: CGPoint(x: center.x, y: center.y + half))
    p.addLine(to: CGPoint(x: center.x + half, y: center.y))
    p.addLine(to: CGPoint(x: center.x, y: center.y - half))
    p.addLine(to: CGPoint(x: center.x - half, y: center.y))
    p.closeSubpath()
    return p
}

// MARK: - Geometry (1024×1024 canvas, y-up CG coordinates)

let S: CGFloat = 1024
// Standard macOS icon grid: 824pt shape centered in the 1024 canvas,
// continuous corners at ~22.37% of the shape size.
let shapeRect = CGRect(x: 100, y: 100, width: 824, height: 824)
let cornerR: CGFloat = 824 * 0.2237

let railX: CGFloat = 312
let railW: CGFloat = 30
let railRect = CGRect(x: railX - railW / 2, y: 262, width: railW, height: 500)

let diamondC = CGPoint(x: railX, y: 700)   // anchor marker (filled diamond)
let midDotC = CGPoint(x: railX, y: 512)    // standard marker (filled circle)
let hollowC = CGPoint(x: railX, y: 324)    // minor marker (hollow circle)
let hollowHoleR: CGFloat = 42              // rail punch-out radius
let hollowRingR: CGFloat = 31              // ring center radius
let hollowRingW: CGFloat = 20

let colLeft: CGFloat = 424                 // right column (prompt + papers)
let paperBar = CGRect(x: colLeft, y: 384, width: 428, height: 124)   // command paper
let derivedBar = CGRect(x: colLeft, y: 274, width: 288, height: 72)  // derived paper
let glyphLeft: CGFloat = 436
let glyphMidY: CGFloat = 672
let glyphHeight: CGFloat = 235

// MARK: - Drawing

func draw(_ ctx: CGContext) {
    let shape = squircle(shapeRect, cornerR)
    ctx.addPath(shape)
    ctx.clip()

    // Background: deep-indigo vertical gradient (top #1A1D2E → bottom #2B3552).
    let bg = CGGradient(colorsSpace: srgb,
                        colors: [rgba(0x1A1D2E), rgba(0x2B3552)] as CFArray,
                        locations: [0, 1])!
    ctx.drawLinearGradient(bg,
                           start: CGPoint(x: S / 2, y: shapeRect.maxY),
                           end: CGPoint(x: S / 2, y: shapeRect.minY),
                           options: [])

    // Soft accent glow behind the prompt glyph.
    let glow = CGGradient(colorsSpace: srgb,
                          colors: [rgba(0x4F6BF0, 0.28), rgba(0x4F6BF0, 0)] as CFArray,
                          locations: [0, 1])!
    let glowC = CGPoint(x: 650, y: 610)
    ctx.drawRadialGradient(glow, startCenter: glowC, startRadius: 0,
                           endCenter: glowC, endRadius: 540, options: [])

    // Faint top sheen.
    let sheen = CGGradient(colorsSpace: srgb,
                           colors: [rgba(0xFFFFFF, 0.07), rgba(0xFFFFFF, 0)] as CFArray,
                           locations: [0, 1])!
    ctx.drawLinearGradient(sheen,
                           start: CGPoint(x: S / 2, y: shapeRect.maxY),
                           end: CGPoint(x: S / 2, y: 600),
                           options: [])

    // Rail: accent capsule with the hollow-marker hole punched out
    // (even-odd clip), gradient #7B93FF (top) → #4F6BF0 (bottom).
    ctx.saveGState()
    let railPath = CGMutablePath()
    railPath.addPath(squircle(railRect, railW / 2))
    railPath.addEllipse(in: CGRect(x: hollowC.x - hollowHoleR, y: hollowC.y - hollowHoleR,
                                   width: hollowHoleR * 2, height: hollowHoleR * 2))
    ctx.addPath(railPath)
    ctx.clip(using: .evenOdd)
    let railGrad = CGGradient(colorsSpace: srgb,
                              colors: [rgba(0x7B93FF), rgba(0x4F6BF0)] as CFArray,
                              locations: [0, 1])!
    ctx.drawLinearGradient(railGrad,
                           start: CGPoint(x: railX, y: railRect.maxY),
                           end: CGPoint(x: railX, y: railRect.minY),
                           options: [])
    ctx.restoreGState()

    // Marker 1 — filled diamond (anchor), white with rounded tips.
    let dia = diamond(center: diamondC, half: 50)
    ctx.setFillColor(rgba(0xFFFFFF))
    ctx.addPath(dia)
    ctx.fillPath()
    ctx.setStrokeColor(rgba(0xFFFFFF))
    ctx.setLineWidth(18)
    ctx.setLineJoin(.round)
    ctx.addPath(dia)
    ctx.strokePath()

    // Marker 2 — filled circle, light accent tint.
    ctx.setFillColor(rgba(0xD6DEFF))
    ctx.fillEllipse(in: CGRect(x: midDotC.x - 42, y: midDotC.y - 42, width: 84, height: 84))

    // Marker 3 — hollow circle ring.
    ctx.setStrokeColor(rgba(0xFFFFFF, 0.88))
    ctx.setLineWidth(hollowRingW)
    ctx.strokeEllipse(in: CGRect(x: hollowC.x - hollowRingR, y: hollowC.y - hollowRingR,
                                 width: hollowRingR * 2, height: hollowRingR * 2))

    // Command paper (high-opacity white block) with a soft drop shadow,
    // then the dimmer derived paper below it.
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 28, color: rgba(0x0A0D1A, 0.45))
    ctx.setFillColor(rgba(0xFFFFFF, 0.90))
    ctx.addPath(squircle(paperBar, 52))
    ctx.fillPath()
    ctx.restoreGState()

    ctx.setFillColor(rgba(0xFFFFFF, 0.35))
    ctx.addPath(squircle(derivedBar, 32))
    ctx.fillPath()

    // Prompt glyph "❯" — bold monospaced system font, sized to a fixed
    // visual height, left-aligned above the command paper.
    func makeLine(_ size: CGFloat) -> CTLine {
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedSystemFont(ofSize: size, weight: .bold),
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): rgba(0xFFFFFF),
        ]
        return CTLineCreateWithAttributedString(NSAttributedString(string: "❯", attributes: attrs))
    }
    ctx.textPosition = .zero
    let probe = CTLineGetImageBounds(makeLine(100), ctx)
    let glyphLine = makeLine(100 * glyphHeight / probe.height)
    ctx.textPosition = .zero
    let gb = CTLineGetImageBounds(glyphLine, ctx)
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -8), blur: 22, color: rgba(0x0A0D1A, 0.35))
    ctx.textPosition = CGPoint(x: glyphLeft - gb.minX, y: glyphMidY - gb.midY)
    CTLineDraw(glyphLine, ctx)
    ctx.restoreGState()
}

// MARK: - Raster + pack

func renderPNG(px: Int, to url: URL) {
    let ctx = CGContext(data: nil, width: px, height: px,
                        bitsPerComponent: 8, bytesPerRow: 0, space: srgb,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.interpolationQuality = .high
    ctx.setAllowsAntialiasing(true)
    ctx.scaleBy(x: CGFloat(px) / S, y: CGFloat(px) / S)
    draw(ctx)
    let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, ctx.makeImage()!, nil)
    CGImageDestinationFinalize(dest)
}

let entries: [(String, Int)] = [
    ("icon_16x16", 16), ("icon_16x16@2x", 32),
    ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256),
    ("icon_256x256", 256), ("icon_256x256@2x", 512),
    ("icon_512x512", 512), ("icon_512x512@2x", 1024),
]
for (name, px) in entries {
    renderPNG(px: px, to: iconset.appendingPathComponent("\(name).png"))
}
print("Rendered \(entries.count) PNGs into \(iconset.path)")

let icns = outDir.appendingPathComponent("AppIcon.icns")
let proc = Process()
proc.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
proc.arguments = ["-c", "icns", iconset.path, "-o", icns.path]
try proc.run()
proc.waitUntilExit()
guard proc.terminationStatus == 0 else {
    FileHandle.standardError.write(Data("iconutil failed (status \(proc.terminationStatus))\n".utf8))
    exit(1)
}
print("Wrote \(icns.path)")
