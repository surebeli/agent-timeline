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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly AppSettings _settings;

    public ProviderSummarizer(AppSettings settings) => _settings = settings;

    public string Name => "provider";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.ProviderBaseUrl) &&
        !string.IsNullOrWhiteSpace(_settings.ProviderModel);

    public async Task<Summary?> SummarizeAsync(UserCommand command, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        var url = _settings.ProviderBaseUrl.TrimEnd('/') + "/chat/completions";
        var body = JsonSerializer.Serialize(new
        {
            model = _settings.ProviderModel,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "user", content = SummaryJson.BuildPrompt(command.Text) },
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
