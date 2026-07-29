import XCTest
@testable import AgentTimeline

/// 文本规整 golden 基准——读 `docs/normalize-cases.tsv`（与 Windows CoreSmokeTest
/// 同一份文件，双端单一事实源，同 design-tokens.json 的同源文化）。
final class NormalizeGoldenTests: XCTestCase {

    /// 仓库内定位：从本测试源文件路径向上找 docs/（swift test 的 CWD 不可依赖）。
    private static func goldenURL() throws -> URL {
        var dir = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        for _ in 0..<8 {
            let candidate = dir.appendingPathComponent("docs/normalize-cases.tsv")
            if FileManager.default.fileExists(atPath: candidate.path) { return candidate }
            dir = dir.deletingLastPathComponent()
        }
        throw XCTSkip("找不到 docs/normalize-cases.tsv")
    }

    /// 转义约定与 Windows runner 逐字一致（顺序也一致，否则 \\n 之类会解歧不同）。
    private static func unescape(_ s: String) -> String {
        s.replacingOccurrences(of: "\\n", with: "\n")
            .replacingOccurrences(of: "\\t", with: "\t")
            .replacingOccurrences(of: "\\e", with: "\u{1B}")
            .replacingOccurrences(of: "\\r", with: "\r")
            .replacingOccurrences(of: "\\\\", with: "\\")
    }

    private static func profile(_ raw: String) -> NormalizeProfile? {
        switch raw {
        case "excerpt": return .excerpt
        case "summary": return .summary
        case "mining": return .mining
        default: return nil
        }
    }

    func testGoldenCases() throws {
        let url = try Self.goldenURL()
        let content = try String(contentsOf: url, encoding: .utf8)
        var executed = 0

        for rawLine in content.components(separatedBy: "\n") {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") { continue }
            let cols = rawLine.components(separatedBy: "\t")
            guard cols.count >= 3, let profile = Self.profile(cols[1]) else { continue }

            let id = cols[0]
            let input = Self.unescape(cols[2])
            let expected = Self.unescape(cols.count > 3 ? cols[3] : "")
            let actual = TextNormalizer.normalize(input, profile: profile)
            XCTAssertEqual(actual, expected, "golden 用例失配: \(id)")

            // 幂等（§3.4-3）：-noidem 用例的输出是回填 verbatim 的裸标记，
            // 再跑一遍自然会被 unwrap，故跳过。
            if !id.hasSuffix("-noidem") {
                XCTAssertEqual(
                    TextNormalizer.normalize(actual, profile: profile), actual,
                    "幂等失败: \(id)")
            }
            executed += 1
        }
        XCTAssertGreaterThanOrEqual(executed, 40, "golden 用例数异常，TSV 是否被截断？")
    }

    /// §3.4-1 空串兜底：整段是围栏时规整为空，必须回退未规整文本而不是清空结果行。
    func testResultExcerptNeverEmpty() {
        let fenceOnly = "```rust\nfn main() {}\n```"
        XCTAssertTrue(TextNormalizer.normalize(fenceOnly, profile: .excerpt).isEmpty)
        XCTAssertFalse(ParserSupport.resultExcerpt(fenceOnly).isEmpty)

        let tableOnly = "| a | b |\n|---|---|"
        XCTAssertTrue(TextNormalizer.normalize(tableOnly, profile: .excerpt).isEmpty)
        XCTAssertFalse(ParserSupport.resultExcerpt(tableOnly).isEmpty)
    }

    /// P1 语义：规整 → 首个非空段落 → ≤500，且不再是「全文拍平截 160」。
    func testResultExcerptFirstParagraph() {
        let text = "**首段结论**已收口。\n\n第二段细节不应进入结果行。"
        XCTAssertEqual(ParserSupport.resultExcerpt(text), "首段结论已收口。")

        let long = String(repeating: "字", count: 700)
        let clipped = ParserSupport.resultExcerpt(long)
        XCTAssertEqual(clipped.count, 501)  // 500 + 省略号
        XCTAssertTrue(clipped.hasSuffix("…"))
    }

    /// §3.3 引子续接：首段以冒号收尾时正文在下一段，只取首段会只剩引子。
    ///
    /// 回归来源：用户库 `TH-0025 是一条安全类 issue,核心是一句话:`——28 字，
    /// 正文（引用块）整段丢失。库内 357 条结果行有 14 条是这个形态。
    func testResultExcerptLeadInContinuation() {
        // 引用块承接：`>` 标记必须剥掉（规整层在 excerpt 档只剥全文首行）
        let quoted = "TH-0025 是一条**安全**类 issue,核心是一句话:\n\n> **正文在这里。**\n\n## 事实核对"
        XCTAssertEqual(
            ParserSupport.resultExcerpt(quoted),
            "TH-0025 是一条安全类 issue,核心是一句话: 正文在这里。")

        // 列表承接：`- ` 同样剥掉
        XCTAssertEqual(
            ParserSupport.resultExcerpt("两个文件已放到桌面：\n\n- 封面.png\n- 成品图.png"),
            "两个文件已放到桌面： 封面.png\n- 成品图.png")

        // 全角冒号同样是引子
        XCTAssertEqual(ParserSupport.resultExcerpt("结论如下：\n\n已收口。"), "结论如下： 已收口。")

        // 非引子首段：行为与修改前逐字节一致（不得续接）
        XCTAssertEqual(
            ParserSupport.resultExcerpt("**首段结论**已收口。\n\n第二段细节不应进入结果行。"),
            "首段结论已收口。")

        // 引子链：连续两层引子继续吃，直到非引子段
        XCTAssertEqual(
            ParserSupport.resultExcerpt("分两步:\n\n第一步:\n\n落地完成。"),
            "分两步: 第一步: 落地完成。")

        // 引子后无正文（全文就一段）→ 保持原样，不得产出尾随空格
        XCTAssertEqual(ParserSupport.resultExcerpt("汇报:"), "汇报:")
    }

    /// P4：存储只留护栏——正常长度的摘要必须原样落库，不再被排版尺寸截断。
    func testStorageLimitsAreGuardRailsNotLayout() {
        // 实测 p90=25、max=41，护栏 120：这个长度的标题必须完整存下来
        let realistic = String(repeating: "标题字", count: 15)  // 45 字
        XCTAssertEqual(ParserSupport.truncate(realistic, to: DisplayLimits.summaryTitle), realistic)

        // 规则摘要：长命令的首行与要点不再被 40/60 咬掉
        let cmd = UserCommand(
            agent: .claude, project: "p", cwd: nil, sessionId: "s",
            timestamp: Date(timeIntervalSince1970: 1_700_000_000),
            text: realistic + "\n" + String(repeating: "要点字", count: 40),
            sourceFile: "/f")
        let summary = RuleSummarizer().summarize(cmd)
        XCTAssertFalse(summary.title.hasSuffix("…"), "护栏不该在正常长度上触发：\(summary.title)")
        XCTAssertFalse(summary.keyPoints.first?.hasSuffix("…") ?? false)

        // 护栏本身仍在：畸形超长输入照样被拦
        let absurd = String(repeating: "长", count: 5000)
        XCTAssertEqual(
            ParserSupport.truncate(absurd, to: DisplayLimits.summaryTitle).count,
            DisplayLimits.summaryTitle + 1)
    }

    /// 截断按 grapheme 计量，代理对 / ZWJ 组合序列不得被劈开（§3.4-4）。
    func testTruncateIsGraphemeSafe() {
        let family = "👨‍👩‍👧"  // 单个 grapheme，内部含代理对与 ZWJ
        let clipped = ParserSupport.resultExcerpt(String(repeating: family, count: 700))
        XCTAssertEqual(clipped.count, 501, "500 grapheme + 省略号")
        XCTAssertTrue(clipped.hasSuffix("…"))
        // 截断点未劈开任何簇：去掉省略号后必须整除为完整的家庭表情
        let body = String(clipped.dropLast())
        XCTAssertEqual(body, String(repeating: family, count: 500))
    }
}
