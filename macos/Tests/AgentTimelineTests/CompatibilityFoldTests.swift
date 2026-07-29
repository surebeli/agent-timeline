import XCTest
@testable import AgentTimeline

/// 匹配态兼容折叠（docs/TEXT-NORMALIZATION.md §3.6）。覆盖点与 Windows
/// `CoreSmokeTest.CompatibilityFold()` 逐条对齐——两端看到的匹配态必须是同一个。
final class CompatibilityFoldTests: XCTestCase {

    /// 半角片假名 + 分离浊点/半浊点 → 全角合成形。分类词表里 デプロイ/バグ/リリース
    /// 这类关键词全是浊音，不合成就整条匹配不上。
    func testFoldForMatch() {
        XCTAssertEqual(TextNormalizer.foldForMatch("ﾃﾞﾌﾟﾛｲ"), "デプロイ")
        XCTAssertEqual(TextNormalizer.foldForMatch("ﾊﾞｸﾞ"), "バグ")
        XCTAssertEqual(TextNormalizer.foldForMatch("ＷＩＰ"), "WIP")
        XCTAssertEqual(TextNormalizer.foldForMatch("Ａ　Ｂ"), "A B")
        // 合不成的浊点原样保留，不得吞字
        XCTAssertEqual(TextNormalizer.foldForMatch("ｱﾞ"), "ア゛")
        // 无兼容字符时必须原样返回
        XCTAssertEqual(TextNormalizer.foldForMatch("完了 done 완료"), "完了 done 완료")
        XCTAssertEqual(TextNormalizer.foldForMatch(""), "")
    }

    /// NFD 组合浊点（U+3099）与独立浊点（U+309B）走同一条合成路径。
    func testCombiningVoiceMarks() {
        XCTAssertEqual(TextNormalizer.foldForMatch("は\u{3099}"), "ば")
        XCTAssertEqual(TextNormalizer.foldForMatch("ハ\u{309B}"), "バ")
        XCTAssertEqual(TextNormalizer.foldForMatch("ハ\u{309A}"), "パ")
        XCTAssertEqual(TextNormalizer.foldForMatch("ウ\u{3099}"), "ヴ")
    }

    func testForMatchLowercases() {
        XCTAssertEqual(TextNormalizer.forMatch("ＷＩＰ Done"), "wip done")
    }

    /// 拉丁关键词必须落在词边界上——不加这条会误命中一批极常见的词。
    /// CJK 不设边界：`N2完成` / `バグ修正` 是自然写法。
    func testWordBoundary() {
        // 会被子串匹配误命中的真实例子
        XCTAssertFalse(TextNormalizer.containsKeyword("prefix", "fix"))
        XCTAssertFalse(TextNormalizer.containsKeyword("suffix", "fix"))
        XCTAssertFalse(TextNormalizer.containsKeyword("networking", "working"))
        XCTAssertFalse(TextNormalizer.containsKeyword("disclosed", "closed"))
        XCTAssertFalse(TextNormalizer.containsKeyword("swipe", "wip"))
        // 正常命中：带标点、在词首/词尾都算
        XCTAssertTrue(TextNormalizer.containsKeyword("bug fix", "fix"))
        XCTAssertTrue(TextNormalizer.containsKeyword("in progress.", "in progress"))
        XCTAssertTrue(TextNormalizer.containsKeyword("fix", "fix"))
        // CJK 不设边界
        XCTAssertTrue(TextNormalizer.containsKeyword("n2完成了", "完成"))
        XCTAssertTrue(TextNormalizer.containsKeyword("バグ修正", "修正"))
        // 多次出现时，前面的被边界否掉不影响后面的真命中
        XCTAssertTrue(TextNormalizer.containsKeyword("prefix and fix", "fix"))
    }

    /// **反证**：把词边界判据短路掉，上面那批必须立刻变红——否则说明用例根本没打到
    /// 这条代码（Windows 侧同样做了这次反证）。
    func testWordBoundaryIsActuallyLoadBearing() {
        let text = Array("prefix".unicodeScalars)
        let keyword = Array("fix".unicodeScalars)
        // 纯子串确实命中（证明用例打到了这条路径上）
        XCTAssertTrue(text.count >= keyword.count)
        XCTAssertFalse(TextNormalizer.hasWordBoundary(text, keyword, 3, 6),
                       "prefix 里的 fix 紧邻拉丁字母，必须被边界判据否掉")
        // 而同样位置换成非拉丁上下文就该放行
        let cjk = Array("修正fix。".unicodeScalars)
        XCTAssertTrue(TextNormalizer.hasWordBoundary(cjk, keyword, 2, 5))
    }

    /// 折叠只服务匹配态，**不得**混进展示管线——展示走 normalize(_:profile:)。
    func testFoldIsNotAppliedToDisplayText() {
        let text = "ＷＩＰ ﾃﾞﾌﾟﾛｲ"
        XCTAssertEqual(TextNormalizer.normalize(text, profile: .excerpt), text)
    }
}
