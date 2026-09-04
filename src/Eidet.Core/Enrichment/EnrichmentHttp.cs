using System.Net.Http.Headers;
using Eidet.Core.Configuration;

namespace Eidet.Core.Enrichment;

/// <summary>
/// The one place that knows how to reach an enrichment backend over HTTP: which path proves it is
/// alive, how its base URL is normalised, and how its bearer token is attached. Shared by the
/// adapters, model discovery, the health monitor and <c>eidet doctor</c>, so a backend that needs
/// auth is reachable from every surface or none.
/// </summary>
public static class EnrichmentHttp
{
    /// <summary>The lightweight liveness endpoint for a provider.</summary>
    public static string ProbePath(EnrichmentProvider provider) =>
        provider == EnrichmentProvider.OpenAiCompatible ? "/v1/models" : "/api/tags";

    /// <summary>
    /// Base URL without trailing slash and without a trailing <c>/v1</c>. The adapters add the
    /// versioned path themselves, and the OpenAI-SDK convention writes the URL <i>with</i>
    /// <c>/v1</c> — a config copied from such a client must not turn into <c>/v1/v1/…</c>.
    /// </summary>
    public static string NormalizeBaseUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^3].TrimEnd('/');
        return trimmed;
    }

    public static HttpClient CreateClient(EnrichmentBackendConfig backend, TimeSpan timeout) =>
        CreateClient(backend.Url, backend.ApiKey, timeout);

    public static HttpClient CreateClient(string url, string? apiKey, TimeSpan timeout)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(NormalizeBaseUrl(url)),
            Timeout = timeout,
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }
}
