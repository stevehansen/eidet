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

    private static readonly TimeSpan MaintenancePollInterval = TimeSpan.FromSeconds(5);

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
        MemoryType? type = null, IEnumerable<string>? tags = null, Valence? valence = null,
        FunctionalStage? stage = null, bool crossRepo = false, CancellationToken ct = default)
    {
        var url = $"api/eidet/search?repo={Uri.EscapeDataString(repo)}&q={Uri.EscapeDataString(query)}&limit={limit}";
        if (type.HasValue) url += $"&type={type.Value.ToString().ToLowerInvariant()}";
        if (valence.HasValue) url += $"&valence={valence.Value.ToString().ToLowerInvariant()}";
        if (stage.HasValue) url += $"&stage={stage.Value.ToString().ToLowerInvariant()}";
        if (tags is not null)
        {
            var joined = string.Join(",", tags);
            if (joined.Length > 0) url += $"&tags={Uri.EscapeDataString(joined)}";
        }
        if (crossRepo) url += "&cross_repo=true";
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

    /// <summary>
    /// Update a memory. Content changes create a versioned supersession; metadata edits update in
    /// place. A stale <see cref="UpdateMemoryRequest.ExpectedContentSha256"/> precondition surfaces
    /// as an <see cref="EidetException"/> with status 409.
    /// </summary>
    public async Task<UpdateResult> UpdateAsync(string id, UpdateMemoryRequest request, CancellationToken ct = default)
    {
        var res = await _http.PutAsJsonAsync($"api/eidet/{Uri.EscapeDataString(id)}", request, JsonOptions, ct);
        return await ReadAsync<UpdateResult>(res, ct);
    }

    /// <summary>Scrub a memory's content to a tombstone (audit node preserved).</summary>
    public async Task<bool> RedactAsync(string id, string reason, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync($"api/eidet/{Uri.EscapeDataString(id)}/redact",
            new { reason }, JsonOptions, ct);
        var data = await ReadAsync<RedactResponse>(res, ct);
        return data.Redacted;
    }

    public async Task<bool> ForgetAsync(string id, string? reason = null, CancellationToken ct = default)
    {
        var url = $"api/eidet/{Uri.EscapeDataString(id)}";
        if (!string.IsNullOrEmpty(reason)) url += $"?reason={Uri.EscapeDataString(reason)}";
        var res = await _http.DeleteAsync(url, ct);
        var data = await ReadAsync<ForgetResponse>(res, ct);
        return data.Forgotten;
    }

    public async Task<bool> FeedbackAsync(string memoryId, bool wasUsed, string? reason = null, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("api/eidet/feedback",
            new { memoryId, wasUsed, reason }, JsonOptions, ct);
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

    public async Task<IntakeResult> IntakeGitAsync(
        string repo, GitIntakeOptions? options = null, bool dryRun = false, CancellationToken ct = default)
    {
        var url = $"api/eidet/intake/git?repo={Uri.EscapeDataString(repo)}";
        if (!string.IsNullOrEmpty(options?.Since)) url += $"&since={Uri.EscapeDataString(options.Since)}";
        if (options?.MaxCommits is { } maxCommits) url += $"&max_commits={maxCommits}";
        if (options?.AllCommits == true) url += "&all_commits=true";
        if (dryRun) url += "&dry_run=true";
        return await PostAsync<IntakeResult>(url, ct);
    }

    /// <summary>Import Claude Code's native per-project memory (MEMORY.md) as seed memories.</summary>
    public async Task<IntakeResult> IntakeClaudeMemoryAsync(string repo, bool dryRun = false, CancellationToken ct = default)
    {
        var url = $"api/eidet/intake/claude-memory?repo={Uri.EscapeDataString(repo)}";
        if (dryRun) url += "&dry_run=true";
        return await PostAsync<IntakeResult>(url, ct);
    }

    public async Task<ConsolidateResult> ConsolidateAsync(string repo, CancellationToken ct = default) =>
        await PostAsync<ConsolidateResult>($"api/eidet/consolidate?repo={Uri.EscapeDataString(repo)}", ct);

    /// <summary>
    /// Runs the maintenance pipeline. A pass that outlives the service's grace window is handed back
    /// as a run id to poll; this follows it to the end, so the result is always the finished report —
    /// a slow repo takes longer, it does not fail.
    /// </summary>
    public async Task<Dictionary<string, JsonElement>> MaintenanceAsync(string repo, CancellationToken ct = default)
    {
        var res = await _http.PostAsync($"api/maintenance?repo={Uri.EscapeDataString(repo)}", null, ct);
        var body = await ReadAsync<Dictionary<string, JsonElement>>(res, ct);
        if (res.StatusCode != System.Net.HttpStatusCode.Accepted) return body;

        var poll = body["poll"].GetString()!.TrimStart('/');
        while (true)
        {
            await Task.Delay(MaintenancePollInterval, ct);
            var run = await GetAsync<MaintenanceRunStatus>(poll, ct);
            if (run.Status == "running") continue;
            if (run.Status == "failed") throw new EidetException(500, run.Error ?? "maintenance failed");
            return run.Report ?? [];
        }
    }

    /// <summary>Render memories as markdown; format "agents" renders the AGENTS.md interop shape.</summary>
    public async Task<string> ExportMarkdownAsync(string repo, string? format = null, CancellationToken ct = default)
    {
        var url = $"api/eidet/export?repo={Uri.EscapeDataString(repo)}";
        if (!string.IsNullOrEmpty(format)) url += $"&format={Uri.EscapeDataString(format)}";
        var res = await _http.GetAsync(url, ct);
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

/// <summary>Envelope returned while polling a maintenance run.</summary>
internal sealed record MaintenanceRunStatus(
    string Status,
    Dictionary<string, JsonElement>? Report = null,
    string? Error = null);

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

public enum Valence { Neutral, Affirming, Refuting, Cautionary }

/// <summary>Functional subtask a memory applies to; <see cref="None"/> matches every stage.</summary>
public enum FunctionalStage { None, Analyze, Locate, Edit, Test, Debug, Deploy }

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
    /// <summary>Shorthand for a dead-end: sets valence=refuting, defaults type to heuristic, tags 'dead-end'.</summary>
    public bool Negative { get; init; }
    /// <summary>Explicit stance toward the subject (overrides <see cref="Negative"/>).</summary>
    public Valence? Valence { get; init; }
    /// <summary>Functional subtask this memory applies to; omit for stage-agnostic knowledge.</summary>
    public FunctionalStage? Stage { get; init; }
}

public record StoreResult
{
    public string? Id { get; init; }
    public string? Error { get; init; }
    public string? DuplicateId { get; init; }
    /// <summary>True when the store contradicted a high-trust memory: stored but downranked until echoed.</summary>
    public bool Quarantined { get; init; }
}

public record UpdateMemoryRequest
{
    /// <summary>New content — creates a versioned supersession; metadata-only edits update in place.</summary>
    public string? Content { get; init; }
    public List<string>? Tags { get; init; }
    public float? Importance { get; init; }
    public float? Confidence { get; init; }
    public MemoryType? Type { get; init; }
    public FunctionalStage? Stage { get; init; }
    public string? OneLiner { get; init; }
    public string? Summary { get; init; }
    public string? ForesightHint { get; init; }
    /// <summary>Optimistic-concurrency precondition: SHA256 of the content you read. Mismatch → 409, no edit.</summary>
    public string? ExpectedContentSha256 { get; init; }
}

public record UpdateResult
{
    public bool Updated { get; init; }
    public string Id { get; init; } = "";
    public bool Superseded { get; init; }
}

public record MemoryEntry
{
    public string Id { get; init; } = "";
    public string RepoId { get; init; } = "";
    public MemoryType Type { get; init; }
    public FunctionalStage Stage { get; init; }
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
    /// <summary>Present on <see cref="EidetClient.GetMemoryAsync"/> only — round-trip as
    /// <see cref="UpdateMemoryRequest.ExpectedContentSha256"/> on a subsequent update.</summary>
    public string? ContentSha256 { get; init; }
}

public record SearchResult
{
    public string Id { get; init; } = "";
    public string RepoId { get; init; } = "";
    public MemoryType Type { get; init; }
    public Valence Valence { get; init; }
    public FunctionalStage Stage { get; init; }
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

/// <summary>Advanced knobs for <see cref="EidetClient.IntakeGitAsync"/>; the happy path passes null.</summary>
public record GitIntakeOptions
{
    /// <summary>Exclusive lower-bound commit SHA (default: the server's per-repo watermark).</summary>
    public string? Since { get; init; }

    /// <summary>Upper bound on commits examined (server default 500).</summary>
    public int? MaxCommits { get; init; }

    /// <summary>Also mine non-Conventional-Commits messages.</summary>
    public bool AllCommits { get; init; }
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
internal record RedactResponse { public bool Redacted { get; init; } }
internal record FeedbackResponse { public bool Applied { get; init; } }
internal record HistoryResponse { public List<MemoryEntry> Chain { get; init; } = []; }
internal record ReposResponse { public List<RepoItem> Repos { get; init; } = []; }
internal record RepoItem { public string RepoId { get; init; } = ""; }
