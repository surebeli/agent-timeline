import Foundation

/// OpenAI-compatible /chat/completions summarizer for custom providers.
struct ProviderSummarizer: Sendable {
    let baseURL: String
    let apiKey: String
    let model: String

    func summarize(_ cmd: UserCommand) async throws -> Summary {
        guard !baseURL.isEmpty, !model.isEmpty else { throw SummarizeError.notConfigured }
        let base = baseURL.hasSuffix("/") ? String(baseURL.dropLast()) : baseURL
        let endpoint = base.hasSuffix("/v1") ? "\(base)/chat/completions" : "\(base)/v1/chat/completions"
        guard let url = URL(string: endpoint) else { throw SummarizeError.notConfigured }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 60
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if !apiKey.isEmpty {
            request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        }
        let body: [String: Any] = [
            "model": model,
            "temperature": 0,
            "messages": [["role": "user", "content": SummaryPrompt.build(for: cmd)]],
        ]
        request.httpBody = try JSONSerialization.data(withJSONObject: body)

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw SummarizeError.httpError((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
        guard let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let choices = obj["choices"] as? [[String: Any]],
              let message = choices.first?["message"] as? [String: Any],
              let content = message["content"] as? String,
              let summary = SummaryPrompt.parse(content, engine: .provider)
        else { throw SummarizeError.badOutput }
        return summary
    }
}
