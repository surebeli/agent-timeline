// 把窗口抓取结果合成到统一画布（深色背板 + 光晕 + 投影），内容居中。
//
// 用法: compose <输入.png> <输出.png> <画布宽px> <画布高px>
//
// 为什么需要这一步：窗口抓取（screencapture -l）取的是窗口自身缓冲区，
// 词典弹层超出面板右缘的那部分背后是空的，会渲染成一块发黑区域并留下接缝。
// 在这里补上背板即可，别试图用屏幕区域截图去"接住"它——那会把盖在上面的
// 第三方全屏浮层一起摄进来（详见 windows/DEBUG-PLAYBOOK.md §3b）。
//
// 视觉常量与 README 首图、社媒图同源，改动前先看那两张是否也要跟着变。
import Cocoa

guard CommandLine.arguments.count == 5,
      let canvasW = Int(CommandLine.arguments[3]),
      let canvasH = Int(CommandLine.arguments[4]) else {
    FileHandle.standardError.write(Data("用法: compose <in.png> <out.png> <w> <h>\n".utf8)); exit(2)
}
let inPath = CommandLine.arguments[1], outPath = CommandLine.arguments[2]

guard let src = NSImage(contentsOfFile: inPath),
      let s = src.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
    FileHandle.standardError.write(Data("读不出输入图: \(inPath)\n".utf8)); exit(1)
}
guard s.width <= canvasW, s.height <= canvasH else {
    FileHandle.standardError.write(Data(
        "画布 \(canvasW)x\(canvasH) 装不下 \(s.width)x\(s.height)\n".utf8)); exit(1)
}

let cs = CGColorSpaceCreateDeviceRGB()
guard let ctx = CGContext(data: nil, width: canvasW, height: canvasH, bitsPerComponent: 8,
                          bytesPerRow: 0, space: cs,
                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { exit(1) }

ctx.setFillColor(CGColor(srgbRed: 0.063, green: 0.063, blue: 0.078, alpha: 1))   // #101014
ctx.fill(CGRect(x: 0, y: 0, width: canvasW, height: canvasH))

func glow(_ cx: Double, _ cy: Double, _ r: Double, _ c: (Double, Double, Double)) {
    let colors = [CGColor(srgbRed: c.0, green: c.1, blue: c.2, alpha: 0.5),
                  CGColor(srgbRed: c.0, green: c.1, blue: c.2, alpha: 0)] as CFArray
    guard let g = CGGradient(colorsSpace: cs, colors: colors, locations: [0, 1]) else { return }
    ctx.drawRadialGradient(g, startCenter: CGPoint(x: cx, y: cy), startRadius: 0,
                           endCenter: CGPoint(x: cx, y: cy), endRadius: r,
                           options: .drawsAfterEndLocation)
}
glow(Double(canvasW) * 0.12, Double(canvasH) * 0.94, Double(canvasW) * 0.55, (0.137, 0.157, 0.220))
glow(Double(canvasW) * 0.95, Double(canvasH) * 0.10, Double(canvasW) * 0.50, (0.114, 0.141, 0.212))

ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -26), blur: 60,
              color: CGColor(srgbRed: 0, green: 0, blue: 0, alpha: 0.55))
ctx.draw(s, in: CGRect(x: (canvasW - s.width) / 2, y: (canvasH - s.height) / 2,
                       width: s.width, height: s.height))
ctx.restoreGState()

guard let out = ctx.makeImage(),
      let png = NSBitmapImageRep(cgImage: out).representation(using: .png, properties: [:]) else { exit(1) }
try png.write(to: URL(fileURLWithPath: outPath))
print("\(canvasW)x\(canvasH) → \((outPath as NSString).lastPathComponent)")
