import XCTest
@testable import AgentTimeline

/// 状态识别词表四语化 + 三条硬伤（docs/TEXT-NORMALIZATION.md §3.6）。
/// 覆盖点对齐 Windows `CoreSmokeTest` 同名用例——两端对同一段文本必须判出同一状态。
final class CodenameFourLanguageTests: XCTestCase {

    private func status(_ text: String, known: Set<String> = ["N1", "N2"]) -> [String: CodenameStatus?] {
        var out: [String: CodenameStatus?] = [:]
        for m in CodenameDetector.detectMentions(in: text, known: known) {
            out[m.name] = m.status
        }
        return out
    }

    // MARK: - 四语状态词

    func testJapaneseStatusWords() {
        XCTAssertEqual(status("N1 の実装が完了しました")["N1"], .completed)
        XCTAssertEqual(status("N1 は対応中です")["N1"], .active)
        XCTAssertEqual(status("N1 の方針転換があった")["N1"], .changed)
    }

    func testKoreanStatusWords() {
        XCTAssertEqual(status("N1 구현 완료했습니다")["N1"], .completed)
        XCTAssertEqual(status("N1 작업 중입니다")["N1"], .active)
        XCTAssertEqual(status("N1 재설계 했습니다")["N1"], .changed)
    }

    func testEnglishStatusWords() {
        XCTAssertEqual(status("N1 is done")["N1"], .completed)
        XCTAssertEqual(status("N1 in progress")["N1"], .active)
        XCTAssertEqual(status("N1 needs rework")["N1"], .changed)
    }

    // MARK: - 硬伤 1：否定的位置三语不同

    /// 日语谓语在句末、否定是词尾——「前两字符」逻辑完全够不着。
    func testJapaneseSuffixNegation() {
        XCTAssertNil(status("N1 はまだ完了していない")["N1"] ?? nil)
        XCTAssertNil(status("N1 の対応は完了しておりません")["N1"] ?? nil)
    }

    /// 韩语两头都有否定。
    func testKoreanNegation() {
        XCTAssertNil(status("N1 완료하지 않았다")["N1"] ?? nil)     // 后置
        XCTAssertNil(status("N1 안 완료")["N1"] ?? nil)             // 前置독립어절
        XCTAssertNil(status("N1 미완료")["N1"] ?? nil)              // 미 紧贴关键词
    }

    /// **最关键的一条**：韩语前置否定按어절判，不能按字符——真实语料里
    /// `이미 완료`(11265) / `제안 완료`(3261) / `잘못`(84805) 都含 안·못·미，
    /// 按字符会把最强的肯定句全杀掉。
    func testKoreanPositivesSurvive() {
        XCTAssertEqual(status("N1 이미 완료")["N1"], .completed, "이미(已经) 含 미，必须放行")
        XCTAssertEqual(status("N1 제안 완료")["N1"], .completed, "제안 含 안，必须放行")
        XCTAssertEqual(status("N1 잘못된 부분 수정 완료")["N1"], .completed, "잘못 含 못，必须放行")
    }

    /// 白名单：「問題ない」整体是肯定评审语，不是"没完成"。
    func testNegationWhitelist() {
        XCTAssertEqual(status("N1 完了、問題ないです")["N1"], .completed)
    }

    /// 中文否定字集不得误伤日语构词汉字。
    func testChineseNegationCharsDoNotHitJapanese() {
        XCTAssertEqual(status("N1 の不具合を修正完了")["N1"], .completed,
                       "不 在索引 0、关键词在索引 4，两字符窗口够不着")
    }

    // MARK: - 硬伤 2：ASCII 句点分句

    /// 韩语句子 100% 用 ASCII 句点收尾——不认它，邻句状态词会串味。
    func testAsciiPeriodSplitsClauses() {
        let s = status("N1 작업 시작. N2 완성.")
        XCTAssertEqual(s["N1"], .active, "N1 只应吃到自己那句的 시작")
        XCTAssertEqual(s["N2"], .completed)
    }

    /// 但 ASCII 句点只在**句末形态**才算——版本号/文件名里的点不能截断窗口。
    func testAsciiPeriodInVersionDoesNotSplit() {
        XCTAssertEqual(status("N1 v0.6.0 완료")["N1"], .completed,
                       "v0.6.0 里的点后面不是空白，不该被当成分句")
    }

    /// 后置否定窗口遇子句边界即止——句号后的否定说的是另一件事。
    func testSuffixNegationStopsAtClauseBreak() {
        XCTAssertEqual(status("N1 完了した。ほかに問題がないか確認")["N1"], .completed)
    }

    // MARK: - 硬伤 3 的连带：拉丁词边界

    /// `prefix` 含 fix、`networking` 含 working、`swipe` 含 wip——都不该命中。
    func testLatinWordBoundary() {
        XCTAssertNil(status("N1 prefix 处理")["N1"] ?? nil)
        XCTAssertNil(status("N1 networking 模块")["N1"] ?? nil)
        XCTAssertNil(status("N1 swipe 手势")["N1"] ?? nil)
        XCTAssertEqual(status("N1 wip")["N1"], .active, "独立的 wip 照常命中")
    }

    // MARK: - 反证：把判据改回去必须立刻打红

    /// 反证 1——关掉后置否定窗口（窗口=0）后，日语否定句会被误判成"完成"。
    /// 这里直接验证窗口逻辑本身：否定标记确实落在关键词之后 8 字内。
    func testSuffixNegationIsLoadBearing() {
        let text = TextNormalizer.forMatch("完了していない")
        let scalars = Array(text.unicodeScalars)
        let kw = Array(TextNormalizer.forMatch("完了").unicodeScalars)
        guard let hit = TextNormalizer.firstIndex(of: kw, in: scalars, from: 0) else {
            return XCTFail("关键词没命中，用例本身失效")
        }
        let tail = String(String.UnicodeScalarView(scalars[(hit + kw.count)...]))
        XCTAssertTrue(tail.contains("ない"), "否定标记必须落在后置窗口能够到的位置")
        XCTAssertLessThanOrEqual(
            tail.distance(from: tail.startIndex, to: tail.range(of: "ない")!.lowerBound), 8,
            "距离超过 8 字窗口，说明这条用例根本打不到后置否定那段代码")
    }

    /// 反证 2——韩语按字符判否定的话，`이미 완료` 会被杀掉。这里确认 `미` 确实
    /// 落在「前两字符」窗口内，即：不加어절判据就一定误杀。
    func testKoreanCharacterLevelWouldMisfire() {
        let text = Array(TextNormalizer.forMatch("이미 완료").unicodeScalars)
        let kw = Array(TextNormalizer.forMatch("완료").unicodeScalars)
        guard let hit = TextNormalizer.firstIndex(of: kw, in: text, from: 0) else {
            return XCTFail("关键词没命中，用例本身失效")
        }
        XCTAssertEqual(Character(text[hit - 2]), "미",
                       "미 就在前两字符窗口内——按字符判必然误杀，故必须按어절")
        // 而实际判定必须放行
        XCTAssertEqual(status("N1 이미 완료")["N1"], .completed)
    }
}
