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

    /// P0 端到端：拿本机真实 session 文件喂 ClaudeParser，slash 命令回显块必须
    /// 产出节点（修复前这些行被整条丢弃）。无语料时跳过。
    func testSlashCommandsSurviveOnRealCorpus() throws {
        let root = ParserSupport.home("~/.claude/projects")
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: root, includingPropertiesForKeys: nil), !files.isEmpty
        else { throw XCTSkip("无本机 Claude 语料") }

        let parser = ClaudeParser()
        var echoLines = 0
        var produced = 0
        var samples: [String] = []

        for dir in files where !dir.path.contains("summarizer") {
            guard let sessions = try? FileManager.default.contentsOfDirectory(
                at: dir, includingPropertiesForKeys: nil) else { continue }
            for session in sessions where session.pathExtension == "jsonl" {
                guard var ctx = parser.makeContext(for: session),
                      let content = try? String(contentsOf: session, encoding: .utf8)
                else { continue }
                for line in content.components(separatedBy: "\n") {
                    // 只认「内容以回显标签开头」的行——正文里提到 <command-name>
                    // 的散文（例如任务书本身）是普通命令，必须原样保留。
                    guard let obj = ParserSupport.json(line),
                          obj["type"] as? String == "user",
                          obj["isMeta"] as? Bool != true,
                          obj["isSidechain"] as? Bool != true,
                          let message = obj["message"] as? [String: Any],
                          let text = Self.extractText(message["content"])?
                              .trimmingCharacters(in: .whitespacesAndNewlines),
                          text.hasPrefix("<command-name>") || text.hasPrefix("<command-message>")
                    else { continue }
                    echoLines += 1
                    if case .userCommand(let cmd)? = parser.parse(line: line, context: &ctx).first {
                        produced += 1
                        XCTAssertTrue(cmd.text.hasPrefix("/"), "转换结果应是 /name[ args]：\(cmd.text)")
                        XCTAssertFalse(cmd.text.contains("<command-"), "不得残留回显标签：\(cmd.text)")
                        if samples.count < 5 { samples.append(cmd.text) }
                    }
                }
            }
        }
        guard echoLines > 0 else { throw XCTSkip("本机语料无 slash 命令回显块") }
        print("[corpus] slash 回显块 \(echoLines) 条 → 产出节点 \(produced) 条；样本 \(samples)")
        XCTAssertEqual(produced, echoLines, "每条回显块都应产出节点（P0 的全部意义）")
    }

    /// 与 ClaudeParser.extractText 同语义的测试侧副本（那边是 private）。
    private static func extractText(_ content: Any?) -> String? {
        if let s = content as? String { return s }
        guard let parts = content as? [[String: Any]] else { return nil }
        let texts = parts.compactMap { part -> String? in
            guard part["type"] as? String == "text" else { return nil }
            return part["text"] as? String
        }
        return texts.isEmpty ? nil : texts.joined(separator: "\n")
    }
}
