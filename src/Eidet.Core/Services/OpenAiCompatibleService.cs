using System.Text.Json;

namespace Eidet.Core.Services;

/// <summary>
/// Model discovery for OpenAI-compatible servers (LM Studio, llama.cpp, vLLM) via
/// <c>GET /v1/models</c>. Counterpart of <see cref="OllamaService"/> for the
/// OpenAiCompatible enrichment provider — list/availability only, no pull:
/// models are managed in the host app (e.g. LM Studio).
/// </summary>
public sealed class OpenAiCompatibleService : IDisposable
{
    private readonly HttpClient _http;

    public OpenAiCompatibleService(string baseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>
    /// Model ids from <c>GET /v1/models</c>, or null when the server is unreachable.
    /// An empty list means the server answered but has no models loaded.
    /// </summary>
    public async Task<List<string>?> TryListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("/v1/models", ct);
            if (!response.IsSuccessStatusCode) return null;
            return ParseModelIds(await response.Content.ReadAsStringAsync(ct));
        }
        catch
        {
            return null;
        }
    }

    internal static List<string> ParseModelIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var model in data.EnumerateArray())
        {
            if (model.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } name)
                result.Add(name);
        }
        return result;
    }

    public void Dispose() => _http.Dispose();
}
