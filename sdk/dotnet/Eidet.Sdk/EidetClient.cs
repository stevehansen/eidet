using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eidet.Sdk;

/// <summary>
/// Client for the Eidet memory service REST API.
/// </summary>
public sealed class EidetClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient _http;

    public EidetClient(string url = "http://localhost:19380", string? apiKey = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/") };
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
    }

    // ─── Core operations ─────────────────────────────────────────

    public async Task<StoreResult> StoreAsync(StoreRequest request, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("api/eidet", request, JsonOptions, ct);
        return await ReadAsync<StoreResult>(res, ct);
    }

    public async Task<List<SearchResult>> RecallAsync(string repo, string query, int limit = 10,
        MemoryType? type = null, CancellationToken ct = default)
    {
        var url = $"api/eidet/search?repo={Uri.EscapeDataString(repo)}&q={Uri.EscapeDataString(query)}&limit={limit}";
        if (type.HasValue) url += $"&type={type.Value.ToString().ToLowerInvariant()}";
        var data = await GetAsync<SearchResponse>(url, ct);
        return data.Results;
    }

    public async Task<string> GetContextAsync(string repo, CancellationToken ct = default)
    {
        var data = await GetAsync<ContextResponse>($"api/eidet/context?repo={Uri.EscapeDataString(repo)}", ct);
        return data.Context;
    }

    public async Task<MemoryEntry> GetMemoryAsync(string id, CancellationToken ct = default) =>
        await GetAsync<MemoryEntry>($"api/eidet/{Uri.EscapeDataString(id)}", ct);

    public async Task<bool> ForgetAsync(string id, string? reason = null, CancellationToken ct = default)
    {
        var url = $"api/eidet/{Uri.EscapeDataString(id)}";
        if (!string.IsNullOrEmpty(reason)) url += $"?reason={Uri.EscapeDataString(reason)}";
        var res = await _http.DeleteAsync(url, ct);
        var data = await ReadAsync<ForgetResponse>(res, ct);
        return data.Forgotten;
    }

    public async Task<bool> FeedbackAsync(string memoryId, bool wasUsed, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("api/eidet/feedback",
            new { memoryId, wasUsed }, JsonOptions, ct);
        var data = await ReadAsync<FeedbackResponse>(res, ct);
        return data.Applied;
    }

    public async Task<List<MemoryEntry>> GetHistoryAsync(string id, CancellationToken ct = default)
    {
        var data = await GetAsync<HistoryResponse>($"api/eidet/history/{Uri.EscapeDataString(id)}", ct);
        return data.Chain;
    }

    // ─── Browse & Graph ──────────────────────────────────────────

    public async Task<BrowseResponse> BrowseAsync(string repo, int skip = 0, int take = 50,
        MemoryType? type = null, CancellationToken ct = default)
    {
        var url = $"api/eidet/browse?repo={Uri.EscapeDataString(repo)}&skip={skip}&take={take}";
        if (type.HasValue) url += $"&type={type.Value.ToString().ToLowerInvariant()}";
        return await GetAsync<BrowseResponse>(url, ct);
    }

    public async Task<GraphData> GetGraphAsync(string repo, int limit = 200, CancellationToken ct = default) =>
        await GetAsync<GraphData>($"api/eidet/graph?repo={Uri.EscapeDataString(repo)}&limit={limit}", ct);

    public async Task<List<string>> GetReposAsync(CancellationToken ct = default)
    {
        var data = await GetAsync<ReposResponse>("api/eidet/repos", ct);
        return data.Repos.Select(r => r.RepoId).ToList();
    }

    // ─── Operations ──────────────────────────────────────────────

    public async Task<IntakeResult> IntakeAsync(string repo, CancellationToken ct = default) =>
        await PostAsync<IntakeResult>($"api/eidet/intake?repo={Uri.EscapeDataString(repo)}", ct);

    public async Task<ConsolidateResult> ConsolidateAsync(string repo, CancellationToken ct = default) =>
        await PostAsync<ConsolidateResult>($"api/eidet/consolidate?repo={Uri.EscapeDataString(repo)}", ct);

    public async Task<string> ExportMarkdownAsync(string repo, CancellationToken ct = default)
    {
        var res = await _http.GetAsync($"api/eidet/export?repo={Uri.EscapeDataString(repo)}", ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    // ─── Usage & Context ────────────────────────────────────────

    public async Task<UsageReport> GetUsageAsync(string repo, int days = 30, CancellationToken ct = default) =>
        await GetAsync<UsageReport>($"api/eidet/usage?repo={Uri.EscapeDataString(repo)}&days={days}", ct);

    public async Task<UsageTimeSeriesResponse> GetUsageTimeSeriesAsync(string repo, string operation,
        int days = 30, CancellationToken ct = default) =>
        await GetAsync<UsageTimeSeriesResponse>(
            $"api/eidet/usage/timeseries?repo={Uri.EscapeDataString(repo)}&operation={Uri.EscapeDataString(operation)}&days={days}", ct);

    public async Task<UsageHourlyResponse> GetUsageHourlyAsync(string repo, int days = 7, CancellationToken ct = default) =>
        await GetAsync<UsageHourlyResponse>(
            $"api/eidet/usage/hourly?repo={Uri.EscapeDataString(repo)}&days={days}", ct);

    public async Task<ContextPreview> GetContextPreviewAsync(string repo, int tokens = 600, CancellationToken ct = default) =>
        await GetAsync<ContextPreview>(
            $"api/eidet/context/preview?repo={Uri.EscapeDataString(repo)}&tokens={tokens}", ct);

    // ─── Health ──────────────────────────────────────────────────

    public async Task<HealthResponse> HealthAsync(CancellationToken ct = default) =>
        await GetAsync<HealthResponse>("api/health", ct);

    public async Task<StatusResponse> StatusAsync(CancellationToken ct = default) =>
        await GetAsync<StatusResponse>("api/status", ct);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            await HealthAsync(ct);
            return true;
        }
        catch { return false; }
    }

    // ─── HTTP helpers ────────────────────────────────────────────

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        var res = await _http.GetAsync(url, ct);
        return await ReadAsync<T>(res, ct);
    }

    private async Task<T> PostAsync<T>(string url, CancellationToken ct)
    {
        var res = await _http.PostAsync(url, null, ct);
        return await ReadAsync<T>(res, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new EidetException((int)res.StatusCode, body);
        }
        return (await res.Content.ReadFromJsonAsync<T>(JsonOptions, ct))!;
    }

    public void Dispose() => _http.Dispose();
}

public class EidetException : Exception
{
    public int StatusCode { get; }
    public string Body { get; }

    public EidetException(int statusCode, string body)
        : base($"Eidet API error {statusCode}: {body}")
    {
        StatusCode = statusCode;
        Body = body;
    }
}

// ─── Request/Response types ──────────────────────────────────────

public enum MemoryType { Observation, Insight, Procedure, Heuristic }

public record StoreRequest
{
    public string Repo { get; init; } = "";
    public string Content { get; init; } = "";
    public MemoryType Type { get; init; }
    public List<string>? Tags { get; init; }
    public float? Importance { get; init; }
    public string? Source { get; init; }
    public string? SessionId { get; init; }
    public string? Supersedes { get; init; }
}

public record StoreResult
{
    public string? Id { get; init; }
    public string? Error { get; init; }
    public string? DuplicateId { get; init; }
}

public record MemoryEntry
{
    public string Id { get; init; } = "";
    public string RepoId { get; init; } = "";
    public MemoryType Type { get; init; }
    public string Content { get; init; } = "";
    public string? Summary { get; init; }
    public string? OneLiner { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<string> Entities { get; init; } = [];
    public float Importance { get; init; }
    public float Confidence { get; init; }
    public int AccessCount { get; init; }
    public int EchoCount { get; init; }
    public int FizzleCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Provenance { get; init; }
    public string? Source { get; init; }
    public string? ForesightHint { get; init; }
}

public record SearchResult
{
    public string Id { get; init; } = "";
    public string RepoId { get; init; } = "";
    public MemoryType Type { get; init; }
    public string Content { get; init; } = "";
    public string? OneLiner { get; init; }
    public List<string> Tags { get; init; } = [];
    public float Importance { get; init; }
    public float Score { get; init; }
    public DateTime CreatedAt { get; init; }
    public int? AgeDays { get; init; }
    public string? StalenessWarning { get; init; }
}

public record BrowseResponse
{
    public string Repo { get; init; } = "";
    public int Skip { get; init; }
    public int Take { get; init; }
    public int Count { get; init; }
    public List<MemoryEntry> Entries { get; init; } = [];
}

public record GraphNode
{
    public string Id { get; init; } = "";
    public MemoryType Type { get; init; }
    public string Label { get; init; } = "";
    public float Importance { get; init; }
    public List<string> Tags { get; init; } = [];
}

public record GraphEdge
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Relation { get; init; } = "";
}

public record GraphData
{
    public List<GraphNode> Nodes { get; init; } = [];
    public List<GraphEdge> Edges { get; init; } = [];
}

public record HealthResponse
{
    public string Status { get; init; } = "";
    public string Version { get; init; } = "";
}

public record StatusResponse
{
    public string Version { get; init; } = "";
    public string Status { get; init; } = "";
    public string Uptime { get; init; } = "";
    public string Api { get; init; } = "";
}

public record IntakeResult
{
    public int NewCount { get; init; }
    public int SkippedCount { get; init; }
}

public record ConsolidateResult
{
    public int Candidates { get; init; }
    public int InsightsCreated { get; init; }
    public int InsightsBoosted { get; init; }
}

public record UsageReport
{
    public string RepoId { get; init; } = "";
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int TotalCalls { get; init; }
    public List<OperationStats> Operations { get; init; } = [];
}

public record OperationStats
{
    public string Operation { get; init; } = "";
    public int CallCount { get; init; }
    public double TotalDurationMs { get; init; }
    public double AvgDurationMs { get; init; }
    public double MaxDurationMs { get; init; }
    public double MinDurationMs { get; init; }
    public int TotalResults { get; init; }
    public DateTime FirstCall { get; init; }
    public DateTime LastCall { get; init; }
}

public record UsageDataPoint
{
    public DateTime Timestamp { get; init; }
    public double DurationMs { get; init; }
    public int ResultCount { get; init; }
}

public record HourlyBucket
{
    public DateTime Hour { get; init; }
    public int TotalCalls { get; init; }
    public Dictionary<string, int> ByOperation { get; init; } = new();
}

public record ContextPreview
{
    public string Repo { get; init; } = "";
    public int MaxTokens { get; init; }
    public string Context { get; init; } = "";
    public int EstimatedTokens { get; init; }
    public List<LayerInfo>? Layers { get; init; }
    public List<string>? CrossRepoScope { get; init; }
}

public record LayerInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
}

public record UsageTimeSeriesResponse
{
    public string Repo { get; init; } = "";
    public string Operation { get; init; } = "";
    public List<UsageDataPoint> Data { get; init; } = [];
}

public record UsageHourlyResponse
{
    public string Repo { get; init; } = "";
    public int Days { get; init; }
    public List<HourlyBucket> Buckets { get; init; } = [];
}

// Internal response wrappers
internal record SearchResponse { public List<SearchResult> Results { get; init; } = []; }
internal record ContextResponse { public string Context { get; init; } = ""; }
internal record ForgetResponse { public bool Forgotten { get; init; } }
internal record FeedbackResponse { public bool Applied { get; init; } }
internal record HistoryResponse { public List<MemoryEntry> Chain { get; init; } = []; }
internal record ReposResponse { public List<RepoItem> Repos { get; init; } = []; }
internal record RepoItem { public string RepoId { get; init; } = ""; }
