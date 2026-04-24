using System.Text.Json;

namespace Eidet.Core.Services;

/// <summary>
/// Ollama model management: list, pull, check availability.
/// Separate from Eidet.Core.Enrichment (which handles enrichment tasks).
/// </summary>
public sealed class OllamaService : IDisposable
{
    private readonly HttpClient _http;

    public OllamaService(string ollamaUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(ollamaUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>Recommended models for Eidet enrichment, in order of preference.</summary>
    public static readonly IReadOnlyList<string> RecommendedModels =
    [
        "gemma4",        // Best balance of quality/speed for enrichment
        "gemma3",        // Older but solid
        "llama3.2",      // Good alternative
        "phi4",          // Microsoft's compact model
        "qwen3",         // Alibaba's model
    ];

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/version", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/version", ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<OllamaModel>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("models", out var models))
                return [];

            var result = new List<OllamaModel>();
            foreach (var model in models.EnumerateArray())
            {
                var name = model.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var size = model.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                var modified = model.TryGetProperty("modified_at", out var m) ? m.GetString() : null;

                result.Add(new OllamaModel
                {
                    Name = name,
                    Size = size,
                    ModifiedAt = modified,
                });
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    public async Task<bool> HasModelAsync(string modelName, CancellationToken ct = default)
    {
        var models = await ListModelsAsync(ct);
        return models.Any(m =>
            m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase) ||
            m.Name.StartsWith(modelName + ":", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Suggests the best available model, or the best recommended model to pull.
    /// Returns (modelName, isInstalled).
    /// </summary>
    public async Task<(string Model, bool IsInstalled)> SuggestModelAsync(CancellationToken ct = default)
    {
        var models = await ListModelsAsync(ct);
        var installed = models.Select(m => m.Name.Split(':')[0].ToLowerInvariant()).ToHashSet();

        // Return first recommended model that's already installed
        foreach (var rec in RecommendedModels)
        {
            if (installed.Contains(rec.ToLowerInvariant()))
                return (rec, true);
        }

        // None installed — suggest the top recommendation
        return (RecommendedModels[0], false);
    }

    /// <summary>
    /// Initiates a model pull. Returns an async enumerable of progress updates.
    /// </summary>
    public async IAsyncEnumerable<PullProgress> PullModelAsync(string modelName, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new { name = modelName }),
            System.Text.Encoding.UTF8,
            "application/json");

        // Use a longer timeout for pulls (models can be large)
        using var pullHttp = new HttpClient
        {
            BaseAddress = _http.BaseAddress,
            Timeout = TimeSpan.FromMinutes(30),
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/pull")
        {
            Content = content,
        };

        var response = await pullHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            yield return new PullProgress { Status = $"Error: HTTP {(int)response.StatusCode}" };
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            PullProgress progress;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                var total = doc.RootElement.TryGetProperty("total", out var t) ? t.GetInt64() : 0;
                var completed = doc.RootElement.TryGetProperty("completed", out var c) ? c.GetInt64() : 0;

                progress = new PullProgress
                {
                    Status = status,
                    Total = total,
                    Completed = completed,
                };
            }
            catch
            {
                progress = new PullProgress { Status = line };
            }

            yield return progress;
        }
    }

    public static string FormatSize(long bytes)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return string.Format(culture, "{0:F1} KB", bytes / 1024.0);
        if (bytes < 1024L * 1024 * 1024) return string.Format(culture, "{0:F1} MB", bytes / (1024.0 * 1024));
        return string.Format(culture, "{0:F1} GB", bytes / (1024.0 * 1024 * 1024));
    }

    public void Dispose() => _http.Dispose();
}

public class OllamaModel
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string? ModifiedAt { get; set; }
}

public class PullProgress
{
    public string Status { get; set; } = "";
    public long Total { get; set; }
    public long Completed { get; set; }
    public double Percent => Total > 0 ? (double)Completed / Total * 100 : 0;
}
