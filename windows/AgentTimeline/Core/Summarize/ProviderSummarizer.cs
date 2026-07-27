using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentTimeline.Core.Summarize;

/// <summary>
/// Custom OpenAI-compatible provider (PRD F4.2): POST {baseUrl}/chat/completions with the
/// user-configured base URL / API key / model from 设置. Response content is parsed with the
/// same strict-JSON contract as the CLI path.
/// </summary>
public sealed class ProviderSummarizer : ISummarizer
{
    // W5 对齐 mac：60s（本地大模型/慢端点 30s 常不够，超时即整条降级规则摘要）。
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly AppSettings _settings;

    public ProviderSummarizer(AppSettings settings) => _settings = settings;

    public string Name => "provider";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.ProviderBaseUrl) &&
        !string.IsNullOrWhiteSpace(_settings.ProviderModel);

    /// <summary>
    /// W5：base URL 不以 /v1 结尾时自动补全（对齐 mac）。用户在设置里填
    /// `https://api.openai.com` 是最常见写法，不补就直接 404。
    /// </summary>
    internal static string BuildChatCompletionsUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) trimmed += "/v1";
        return trimmed + "/chat/completions";
    }

    public async Task<Summary?> SummarizeAsync(UserCommand command, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        var url = BuildChatCompletionsUrl(_settings.ProviderBaseUrl);
        var body = JsonSerializer.Serialize(new
        {
            model = _settings.ProviderModel,
            // W5 对齐 mac：0 而非 0.2——摘要要的是可复现，同命令重跑不该换标题。
            temperature = 0,
            messages = new object[]
            {
                new { role = "user", content = SummaryJson.BuildPrompt(command) },
            },
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(_settings.ProviderApiKey))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", _settings.ProviderApiKey);
            }

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"ProviderSummarizer: HTTP {(int)response.StatusCode} from {url}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return SummaryJson.Parse(content ?? "", SummarySource.Provider);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn("ProviderSummarizer: request timed out");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("ProviderSummarizer failed", ex);
            return null;
        }
    }
}
