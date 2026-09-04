using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eidet.Core.Enrichment;

/// <summary>
/// Enrichment over any OpenAI-compatible server (LM Studio, llama.cpp server, vLLM — local or a
/// private network cluster behind a bearer token): <c>GET /v1/models</c> for health,
/// <c>POST /v1/chat/completions</c> for chat. The answer is read from
/// <c>choices[0].message.content</c>; a reasoning model's thoughts arrive either in a separate
/// field this adapter never reads, or inline as <c>&lt;think&gt;</c> blocks the sanitizer strips.
/// </summary>
internal sealed class OpenAiEnrichmentAdapter : IEnrichmentPort, IDisposable
{
    private readonly HttpClient _http;
    private readonly EnrichmentHealthCache _health;
    private readonly string _model;
    private readonly bool? _thinking;

    public OpenAiEnrichmentAdapter(string baseUrl, string model, string? apiKey = null, bool? thinking = null)
    {
        _model = model;
        _thinking = thinking;
        _http = EnrichmentHttp.CreateClient(baseUrl, apiKey, TimeSpan.FromSeconds(120));
        _health = new EnrichmentHealthCache(_http);
    }

    public bool IsAvailable => _health.IsAvailable;

    public string? ModelName => _model;

    public Task<bool> CheckHealthAsync(CancellationToken ct = default)
        => _health.CheckAsync("/v1/models", ct);

    public async Task<string?> CompleteAsync(EnrichmentRequest request, CancellationToken ct = default)
    {
        if (!await CheckHealthAsync(ct)) return null;
        return await ChatAsync(EnrichmentPrompts.Build(request), ct);
    }

    /// <summary>
    /// The request body. <c>chat_template_kwargs.thinking</c> is only present when configured: it is
    /// a vLLM/llama.cpp template extension, and a gateway that validates its schema strictly would
    /// reject a field it does not know — so an unset <paramref name="thinking"/> puts nothing on the wire.
    /// </summary>
    internal static string BuildPayload(string model, string prompt, bool? thinking)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["stream"] = false,
        };
        if (thinking is { } think)
            payload["chat_template_kwargs"] = new JsonObject { ["thinking"] = think };
        return payload.ToJsonString();
    }

    private async Task<string?> ChatAsync(string prompt, CancellationToken ct)
    {
        try
        {
            var json = BuildPayload(_model, prompt, _thinking);
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
