import Foundation

/// Shared prompt + strict-JSON response contract for CLI and provider engines.
enum SummaryPrompt {
    struct Payload: Codable {
        var title: String
        var keyPoints: [String]?
        var codenames: [CodenameDef]?
        var resultLine: String?
    }

    static func build(for cmd: UserCommand) -> String {
        let text = ParserSupport.truncate(cmd.text, to: 4000)
        return """
        你是一个命令摘要器。下面是用户在 \(cmd.agent.displayName) 中提交的一条命令（项目：\(cmd.project)）。\
        请只输出一个 JSON 对象（不要 markdown 代码块、不要任何解释），字段如下：
        {"title": "≤20字的标题，概括这条命令要做什么",
         "keyPoints": ["关键点/需求点/任务点，每条≤30字，最多5条；命令简单时可为空数组"],
         "codenames": [{"name": "命令中出现的任务代号/需求代号，如 T-PLUGIN-00；没有则空数组", "definition": "该代号在本命令语境中的含义，≤40字"}],
         "resultLine": null}

        用户命令原文：
        ---
        \(text)
        ---
        """
    }

    /// Accepts raw model output that may be wrapped in code fences or prose.
    static func parse(_ raw: String, engine: SummaryEngineKind) -> Summary? {
        guard let jsonString = extractJSONObject(raw),
              let data = jsonString.data(using: .utf8),
              let payload = try? JSONDecoder().decode(Payload.self, from: data),
              !payload.title.trimmingCharacters(in: .whitespaces).isEmpty
        else { return nil }
        return Summary(
            title: ParserSupport.truncate(payload.title, to: 60),
            keyPoints: (payload.keyPoints ?? []).prefix(6).map { ParserSupport.truncate($0, to: 80) },
            codenames: payload.codenames ?? [],
            resultLine: payload.resultLine,
            engine: engine.rawValue)
    }

    private static func extractJSONObject(_ raw: String) -> String? {
        guard let start = raw.firstIndex(of: "{"),
              let end = raw.lastIndex(of: "}"),
              start < end else { return nil }
        return String(raw[start...end])
    }
}
