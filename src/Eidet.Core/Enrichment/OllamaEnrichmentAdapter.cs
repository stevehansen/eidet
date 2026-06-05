using System.Text;
using System.Text.Json;

namespace Eidet.Core.Enrichment;

internal sealed class OllamaEnrichmentAdapter : IEnrichmentPort, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly EnrichmentHealthCache _health;
    private readonly string _model;

    public OllamaEnrichmentAdapter(string ollamaUrl, string model)
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri(ollamaUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(120),
        };
        _health = new EnrichmentHealthCache(_http);
    }

    public bool IsAvailable => _health.IsAvailable;

    public Task<bool> CheckHealthAsync(CancellationToken ct = default)
        => _health.CheckAsync("/api/tags", ct);

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
                think = false,
            };

            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("/api/chat", httpContent, ct);

            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);

            if (doc.RootElement.TryGetProperty("message", out var msg) &&
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
