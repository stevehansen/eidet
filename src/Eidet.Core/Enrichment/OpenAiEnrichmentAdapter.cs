using System.Text;
using System.Text.Json;

namespace Eidet.Core.Enrichment;

/// <summary>
/// Enrichment over any OpenAI-compatible local server (LM Studio, llama.cpp server, vLLM):
/// <c>GET /v1/models</c> for health, <c>POST /v1/chat/completions</c> for chat. No Ollama-only
/// <c>think</c> field; response is read from <c>choices[0].message.content</c>.
/// </summary>
internal sealed class OpenAiEnrichmentAdapter : IEnrichmentPort, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly EnrichmentHealthCache _health;
    private readonly string _model;

    public OpenAiEnrichmentAdapter(string baseUrl, string model)
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(120),
        };
        _health = new EnrichmentHealthCache(_http);
    }

    public bool IsAvailable => _health.IsAvailable;

    public Task<bool> CheckHealthAsync(CancellationToken ct = default)
        => _health.CheckAsync("/v1/models", ct);

    public async Task<string?> CompleteAsync(EnrichmentRequest request, CancellationToken ct = default)
    {
        if (!await CheckHealthAsync(ct)) return null;
        return await ChatAsync(EnrichmentPrompts.Build(request), ct);
    }

    private async Task<string?> ChatAsync(string prompt, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model = _model,
                messages = new[] { new { role = "user", content = prompt } },
                stream = false,
            };

            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("/v1/chat/completions", httpContent, ct);

            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);

            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content))
            {
                var text = OllamaTextSanitizer.Clean(content.GetString()?.Trim());
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
