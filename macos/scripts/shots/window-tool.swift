// 截图编排用的窗口/输入工具（配合 shoot-readme.sh）。
//
// 用法:
//   window-tool list            列出 Agent Timeline 的全部在屏窗口: "<id> <x> <y> <w> <h>"
//                               （top-left 坐标系，与 screencapture -R 同源；面板恒为最后一行）
//   window-tool click <x> <y>   在屏幕坐标合成一次左键点击
//   window-tool move  <x> <y>   仅移动指针（用于让 hover 态复位、并让 tooltip 消失）
//   window-tool up              补发一次 mouseUp——中断的拖拽会残留按下态，
//                               之后任何 move 都会把面板拖走
//   window-tool scroll <x> <y> <deltaY> [count]
//                               在屏幕坐标合成 [count]（默认 1）次滚轮事件，每次间隔
//                               30ms；deltaY 负值向下滚（内容往上移，等价用户往下拉）。
//                               用于交互测试滚动触发的行为（例如滚到底自动加载）。
//
// ⚠ 必须先 swiftc 编译成二进制再用：`swift window-tool.swift` 每次都重新编译，
//   几个交互步骤串起来就会超时（实测 >2min）。shoot-readme.sh 已代劳。
import Cocoa

func windows() -> [[String: Any]] {
    let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]]
    return (list ?? []).filter { ($0[kCGWindowOwnerName as String] as? String) == "Agent Timeline" }
}

func point(_ args: ArraySlice<String>) -> CGPoint {
    guard args.count >= 2, let x = Double(args[args.startIndex]),
          let y = Double(args[args.startIndex + 1]) else {
        FileHandle.standardError.write(Data("需要 <x> <y>\n".utf8)); exit(2)
    }
    return CGPoint(x: x, y: y)
}

func post(_ type: CGEventType, _ p: CGPoint) {
    CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: p, mouseButton: .left)?
        .post(tap: .cghidEventTap)
}

let args = CommandLine.arguments
guard args.count >= 2 else {
    FileHandle.standardError.write(Data("用法: window-tool list|click|move|up [x y]\n".utf8)); exit(2)
}

switch args[1] {
case "list":
    for w in windows() {
        guard let b = w[kCGWindowBounds as String] as? [String: CGFloat],
              let id = w[kCGWindowNumber as String] else { continue }
        print("\(id) \(Int(b["X"] ?? 0)) \(Int(b["Y"] ?? 0)) \(Int(b["Width"] ?? 0)) \(Int(b["Height"] ?? 0))")
    }
case "click":
    let p = point(args[2...])
    post(.mouseMoved, p); usleep(200_000)
    post(.leftMouseDown, p); usleep(80_000)
    post(.leftMouseUp, p)
case "move":
    post(.mouseMoved, point(args[2...]))
case "up":
    post(.leftMouseUp, CGEvent(source: nil)?.location ?? .zero)
case "scroll":
    guard args.count >= 5, let dy = Int32(args[4]) else {
        FileHandle.standardError.write(Data("用法: window-tool scroll <x> <y> <deltaY> [count]\n".utf8))
        exit(2)
    }
    let p = point(args[2...3])
    let count = args.count >= 6 ? (Int(args[5]) ?? 1) : 1
    post(.mouseMoved, p)
    usleep(100_000)
    for _ in 0..<count {
        if let scroll = CGEvent(
            scrollWheelEvent2Source: nil, units: .pixel, wheelCount: 1,
            wheel1: dy, wheel2: 0, wheel3: 0
        ) {
            scroll.location = p
            scroll.post(tap: .cghidEventTap)
        }
        usleep(30_000)
    }
default:
    FileHandle.standardError.write(Data("未知子命令: \(args[1])\n".utf8)); exit(2)
}
