using System.Text;
using System.Text.Json;

namespace Eidet.Core.Enrichment;

internal sealed class OllamaEnrichmentAdapter : IEnrichmentPort, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly TimeSpan HealthCacheDuration = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _model;
    private bool? _lastHealthy;
    private DateTime _lastHealthCheck = DateTime.MinValue;

    public OllamaEnrichmentAdapter(string ollamaUrl, string model)
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri(ollamaUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    public bool IsAvailable => _lastHealthy == true ||
        (_lastHealthy == null && DateTime.UtcNow - _lastHealthCheck > HealthCacheDuration);

    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastHealthCheck < HealthCacheDuration && _lastHealthy.HasValue)
            return _lastHealthy.Value;

        try
        {
            var response = await _http.GetAsync("/api/tags", ct);
            _lastHealthy = response.IsSuccessStatusCode;
        }
        catch
        {
            _lastHealthy = false;
        }

        _lastHealthCheck = DateTime.UtcNow;
        return _lastHealthy.Value;
    }

    public async Task<string?> CompleteAsync(EnrichmentRequest request, CancellationToken ct = default)
    {
        if (!await CheckHealthAsync(ct)) return null;

        var prompt = BuildPrompt(request);
        return await ChatAsync(prompt, ct);
    }

    private static string BuildPrompt(EnrichmentRequest request) => request.Kind switch
    {
        EnrichmentPrompt.OneLiner => $"""
            Generate an ultra-compact one-liner summary (~10 words max) of this memory.
            Return ONLY the one-liner, nothing else.

            Memory: {request.Primary}
            """,

        EnrichmentPrompt.Summary => $"""
            Summarize this memory in 1-2 concise sentences for a software developer.
            Return ONLY the summary, nothing else.

            Memory: {request.Primary}
            """,

        EnrichmentPrompt.ForesightHint => $"""
            Given this developer memory, predict WHEN and HOW it will be most useful in the future.
            Write a brief foresight hint (1 sentence) that helps an AI agent know when to surface this memory.
            Return ONLY the hint, nothing else.

            Memory: {request.Primary}
            """,

        EnrichmentPrompt.Entities => $"""
            Extract named entities from this developer memory: project names, package names,
            file paths, class names, function names, API endpoints, configuration keys, error codes.
            Return one entity per line, nothing else. If none found, return empty.

            Text: {request.Primary}
            """,

        EnrichmentPrompt.MergeObservations => BuildMergePrompt(request.Aux ?? []),

        _ => request.Primary,
    };

    private static string BuildMergePrompt(IReadOnlyList<string> observations)
    {
        var numbered = string.Join("\n", observations.Select((o, i) => $"{i + 1}. {o}"));
        return $"""
            These related developer observations should be merged into a single coherent insight.
            Write a concise insight (2-3 sentences) that captures the essential knowledge.
            Return ONLY the merged insight, nothing else.

            Observations:
            {numbered}
            """;
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
            var response = await _http.PostAsync("/api/chat", httpContent, ct);

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
