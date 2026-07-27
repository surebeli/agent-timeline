import Foundation

/// Shared prompt + strict-JSON response contract for CLI and provider engines.
enum SummaryPrompt {
    struct CodenamePayload: Codable {
        var name: String
        var definition: String?
        var status: String?
    }

    struct Payload: Codable {
        var title: String
        var keyPoints: [String]?
        var codenames: [CodenamePayload]?
        var resultLine: String?
        var kind: String?
    }

    static func build(for cmd: UserCommand) -> String {
        let text = ParserSupport.truncate(cmd.text, to: DisplayLimits.promptInput)
        return """
        你是一个命令摘要器。下面是用户在 \(cmd.agent.displayName) 中提交的一条命令（项目：\(cmd.project)）。\
        请只输出一个 JSON 对象（不要 markdown 代码块、不要任何解释），字段如下：
        {"title": "≤20字的标题，概括这条命令要做什么",
         "kind": "按命令主要意图归类，取 需求|任务|调研|学习|决策|修复|其他 之一",
         "keyPoints": ["关键点/需求点/任务点，每条≤30字，最多5条；命令简单时可为空数组"],
         "codenames": [{"name": "命令中出现的需求/任务/里程碑代号，短码（N1、T2、M1）和长码（REQ-3、T-PLUGIN-00）都算；没有则空数组",
                        "definition": "该代号指代的具体内容，≤40字；若本命令只是提及或更新状态而没有给出定义，留空字符串",
                        "status": "该代号在本命令中的生命周期信号，取 定义|进行中|完成|变更|提及 之一"}],
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
        let codenames = (payload.codenames ?? []).compactMap { item -> CodenameDef? in
            let name = item.name.trimmingCharacters(in: .whitespaces)
            guard CodenameDetector.isPlausibleName(name) else { return nil }
            return CodenameDef(
                name: name,
                definition: ParserSupport.truncate(item.definition ?? "", to: DisplayLimits.codenameDefinition),
                status: item.status)
        }
        let kind = payload.kind.flatMap { NodeKind(rawValue: $0)?.rawValue }
        return Summary(
            title: ParserSupport.truncate(payload.title, to: DisplayLimits.summaryTitle),
            keyPoints: (payload.keyPoints ?? []).prefix(DisplayLimits.keyPointCount)
                .map { ParserSupport.truncate($0, to: DisplayLimits.keyPoint) },
            codenames: codenames,
            resultLine: payload.resultLine,
            engine: engine.rawValue,
            kind: kind)
    }

    private static func extractJSONObject(_ raw: String) -> String? {
        guard let start = raw.firstIndex(of: "{"),
              let end = raw.lastIndex(of: "}"),
              start < end else { return nil }
        return String(raw[start...end])
    }
}
