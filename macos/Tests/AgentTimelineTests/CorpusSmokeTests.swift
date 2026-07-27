import XCTest
@testable import AgentTimeline

/// 真实语料冒烟：规整器在本机 agent 语料上不得崩溃、不得把非空文本变成空结果行、
/// 不得异常膨胀。语料文件不存在时跳过（CI 上没有 ~/.claude 语料）。
final class CorpusSmokeTests: XCTestCase {

    func testNormalizeOnRealCorpus() throws {
        let path = "/tmp/corpus-sample.json"
        guard FileManager.default.fileExists(atPath: path),
              let data = FileManager.default.contents(atPath: path),
              let texts = try? JSONSerialization.jsonObject(with: data) as? [String],
              !texts.isEmpty
        else { throw XCTSkip("无本机语料样本（CI 环境正常跳过）") }

        var emptied = 0
        for text in texts {
            let excerpt = ParserSupport.resultExcerpt(text)
            XCTAssertFalse(
                excerpt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                "结果行被清空（§3.4-1 兜底失效）：\(text.prefix(60))")
            XCTAssertLessThanOrEqual(excerpt.count, 501)
            if TextNormalizer.normalize(text, profile: .excerpt).isEmpty { emptied += 1 }

            // 幂等：规整两次结果一致
            let once = TextNormalizer.normalize(text, profile: .excerpt)
            XCTAssertEqual(TextNormalizer.normalize(once, profile: .excerpt), once)
        }
        print("[corpus] \(texts.count) 条；其中 \(emptied) 条规整后为空（走兜底）")
    }
}
