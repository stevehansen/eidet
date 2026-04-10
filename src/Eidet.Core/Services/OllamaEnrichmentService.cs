using System.Text;
using System.Text.Json;

namespace Eidet.Core.Services;

/// <summary>
/// Ollama-backed enrichment service. Uses /api/chat with think:false.
/// 120s timeout for cold starts. Lazy health re-check.
/// All enrichment is additive and async — core memory works without it.
/// </summary>
public sealed class OllamaEnrichmentService : IEnrichmentService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly string _model;
    private bool? _lastHealthy;
    private DateTime _lastHealthCheck = DateTime.MinValue;
    private static readonly TimeSpan HealthCacheDuration = TimeSpan.FromMinutes(5);

    public OllamaEnrichmentService(string ollamaUrl, string model)
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri(ollamaUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(120), // Cold start tolerance
        };
    }

    public bool IsAvailable => _lastHealthy == true ||
        (_lastHealthy == null && DateTime.UtcNow - _lastHealthCheck > HealthCacheDuration);

    public async Task<string?> GenerateOneLinerAsync(string content, CancellationToken ct = default)
    {
        var prompt = $"""
            Generate an ultra-compact one-liner summary (~10 words max) of this memory.
            Return ONLY the one-liner, nothing else.

            Memory: {content}
            """;
        return await ChatAsync(prompt, ct);
    }

    public async Task<string?> GenerateSummaryAsync(string content, CancellationToken ct = default)
    {
        var prompt = $"""
            Summarize this memory in 1-2 concise sentences for a software developer.
            Return ONLY the summary, nothing else.

            Memory: {content}
            """;
        return await ChatAsync(prompt, ct);
    }

    public async Task<string?> GenerateForesightHintAsync(string content, CancellationToken ct = default)
    {
        var prompt = $"""
            Given this developer memory, predict WHEN and HOW it will be most useful in the future.
            Write a brief foresight hint (1 sentence) that helps an AI agent know when to surface this memory.
            Return ONLY the hint, nothing else.

            Memory: {content}
            """;
        return await ChatAsync(prompt, ct);
    }

    public async Task<List<string>> ExtractEntitiesAsync(string content, CancellationToken ct = default)
    {
        var prompt = $"""
            Extract named entities from this developer memory: project names, package names,
            file paths, class names, function names, API endpoints, configuration keys, error codes.
            Return one entity per line, nothing else. If none found, return empty.

            Text: {content}
            """;
        var result = await ChatAsync(prompt, ct);
        if (string.IsNullOrWhiteSpace(result)) return [];

        return result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.Length > 1 && e.Length < 100)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> MergeObservationsAsync(List<string> observations, CancellationToken ct = default)
    {
        var numbered = string.Join("\n", observations.Select((o, i) => $"{i + 1}. {o}"));
        var prompt = $"""
            These related developer observations should be merged into a single coherent insight.
            Write a concise insight (2-3 sentences) that captures the essential knowledge.
            Return ONLY the merged insight, nothing else.

            Observations:
            {numbered}
            """;
        return await ChatAsync(prompt, ct);
    }

    public async Task<string?> DetectConflictAsync(string newContent, string existingContent, CancellationToken ct = default)
    {
        var prompt = $"""
            Do these two developer memories contradict each other?
            If yes, explain the contradiction in one sentence. If no, respond with exactly "NO_CONFLICT".

            Memory A: {newContent}
            Memory B: {existingContent}
            """;
        var result = await ChatAsync(prompt, ct);
        if (string.IsNullOrWhiteSpace(result) || result.Contains("NO_CONFLICT", StringComparison.OrdinalIgnoreCase))
            return null;
        return result;
    }

    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        // Use cached result if recent
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

    private async Task<string?> ChatAsync(string prompt, CancellationToken ct)
    {
        if (!await CheckHealthAsync(ct))
            return null;

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
                var text = content.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            return null;
        }
        catch
        {
            // Enrichment is never critical
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
