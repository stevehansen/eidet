using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

/// <summary>
/// Public surface that AI agents and the API/MCP layers use to talk to memory.
/// Thin facade over three internal collaborators: <see cref="MemoryWriter"/> for
/// mutations, <see cref="MemoryRecall"/> for the read pipeline (recall + L0/L1
/// context), and <see cref="MemoryQueries"/> for inspection lookups. The shared
/// <see cref="RecallCache"/> and <see cref="RepoActivityTracker"/> wire the
/// writer's invalidations into the reader's cache.
/// </summary>
public class MemoryService
{
    private readonly RecallCache _cache = new();
    private readonly RepoActivityTracker _activity = new();
    private readonly MemoryWriter _writer;
    private readonly MemoryRecall _recall;
    private readonly MemoryQueries _queries;

    public int StalenessWarningDays { get; set; } = 7;

    public MemoryService(IEidetStore store, LayerService? layers = null, IHookRunner? hooks = null)
    {
        var hookRunner = hooks ?? NullHookRunner.Instance;
        _writer = new MemoryWriter(store, hookRunner, _cache, _activity);
        _recall = new MemoryRecall(store, layers, hookRunner, _cache, _activity, () => StalenessWarningDays);
        _queries = new MemoryQueries(store);
    }

    public Task<StoreResult> StoreAsync(
        string repoId,
        string content,
        MemoryType type,
        List<string>? tags = null,
        float importance = 0.5f,
        string source = "claude-session",
        string? sessionId = null,
        string? supersedes = null,
        MemoryProvenance? provenance = null,
        CancellationToken ct = default) =>
        _writer.StoreAsync(repoId, content, type, tags, importance, source, sessionId, supersedes, provenance, ct);

    public Task<List<MemorySearchResult>> RecallAsync(string repoId, MemoryQuery query, CancellationToken ct = default) =>
        _recall.RecallAsync(repoId, query, ct);

    public Task<string> GetContextAsync(string repoId, int maxTokens = 600, CancellationToken ct = default) =>
        _recall.GetContextAsync(repoId, maxTokens, ct);

    public Task<bool> ForgetAsync(string id, string? reason = null, string? sessionId = null, CancellationToken ct = default) =>
        _writer.ForgetAsync(id, reason, sessionId, ct);

    public Task<bool> ApplyFeedbackAsync(string memoryId, bool wasUsed, CancellationToken ct = default) =>
        _writer.ApplyFeedbackAsync(memoryId, wasUsed, ct);

    public Task<List<MemoryEntry>> GetVersionChainAsync(string memoryId, CancellationToken ct = default) =>
        _queries.GetVersionChainAsync(memoryId, ct);

    public Task<DatabaseInfo?> GetStoreInfoAsync(CancellationToken ct = default) =>
        _queries.GetStoreInfoAsync(ct);

    public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default) =>
        _queries.GetCountsByTypeAsync(repoId, ct);

    public Task<bool> UpdateMemoryAsync(
        string id,
        string? content = null,
        List<string>? tags = null,
        float? importance = null,
        float? confidence = null,
        MemoryType? type = null,
        string? oneLiner = null,
        string? summary = null,
        string? foresightHint = null,
        CancellationToken ct = default) =>
        _writer.UpdateAsync(id, content, tags, importance, confidence, type, oneLiner, summary, foresightHint, ct);

    public Task<bool> AddLinkAsync(
        string memoryId, string targetRepoId, string relation, string? targetMemoryId = null, CancellationToken ct = default) =>
        _writer.AddLinkAsync(memoryId, targetRepoId, relation, targetMemoryId, ct);

    public Task<bool> RemoveLinkAsync(string memoryId, string targetRepoId, string relation, CancellationToken ct = default) =>
        _writer.RemoveLinkAsync(memoryId, targetRepoId, relation, ct);

    public Task<List<MemoryEntry>> BrowseAsync(
        string repoId, int skip = 0, int take = 50, MemoryType? type = null, CancellationToken ct = default) =>
        _queries.BrowseAsync(repoId, skip, take, type, ct);

    public Task<List<string>> GetRepoIdsAsync(CancellationToken ct = default) =>
        _queries.GetRepoIdsAsync(ct);

    public Task<GraphData> GetGraphDataAsync(string repoId, int limit = 200, CancellationToken ct = default) =>
        _queries.GetGraphDataAsync(repoId, limit, ct);

    public bool IsRepoActive(string repoId, int withinDays = 7) =>
        _activity.IsActive(repoId, withinDays);
}
