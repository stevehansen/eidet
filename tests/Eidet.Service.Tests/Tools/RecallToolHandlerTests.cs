using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.LooseEnds;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

public class RecallToolHandlerTests
{
    [Fact]
    public async Task Recall_NoMatches_ReturnsOkWithEmptyResults()
    {
        var handler = NewHandler(out _);

        var result = await Invoke(handler, new { query = "anything" });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal(0, result.ResultCount);
        Assert.Equal("No memories found.", result.HumanSummary);
        Assert.NotNull(result.Payload);
    }

    [Fact]
    public async Task Recall_WithHits_ReturnsCountAndPrefixedLines()
    {
        var handler = NewHandler(out var store);
        store.NextResults =
        [
            new MemoryEntry
            {
                Id = "memories/r/insight/abc",
                RepoId = "r",
                Type = MemoryType.Insight,
                Content = "RavenDB is the persistence layer",
                OneLiner = "RavenDB persistence",
                Importance = 0.7f,
                CreatedAt = DateTime.UtcNow,
            },
        ];

        var result = await Invoke(handler, new { query = "ravendb" });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal(1, result.ResultCount);
        Assert.Contains("1 memory(ies) found:", result.HumanSummary);
        Assert.Contains("[I] RavenDB persistence", result.HumanSummary);
        Assert.Contains("id=memories/r/insight/abc", result.HumanSummary);
    }

    [Fact]
    public async Task Recall_WithMatchingLooseEndTags_IncludesRideAlongSection()
    {
        var endStore = new FakeLooseEndStore();
        var looseEnds = new LooseEndService(endStore, new FakePromotionPort(), TimeProvider.System);
        await looseEnds.ParkAsync(new ParkOptions("test-repo", "revisit the retry backoff in the auth client")
        {
            Tags = ["auth", "retry"],
        });

        var svc = new MemoryService(new RecallStore());
        var handler = new RecallToolHandler(svc, looseEnds);

        var result = await Invoke(handler, new { query = "auth", tags = new[] { "auth" } });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Contains("open loose end(s) matching your tags", result.HumanSummary);
        Assert.Contains("[~] revisit the retry backoff in the auth client", result.HumanSummary);
    }

    [Fact]
    public async Task Recall_NoTags_OmitsRideAlongSection()
    {
        var endStore = new FakeLooseEndStore();
        var looseEnds = new LooseEndService(endStore, new FakePromotionPort(), TimeProvider.System);
        await looseEnds.ParkAsync(new ParkOptions("test-repo", "revisit the retry backoff in the auth client")
        {
            Tags = ["auth"],
        });

        var svc = new MemoryService(new RecallStore());
        var handler = new RecallToolHandler(svc, looseEnds);

        // No tags on the query → ride-along is tag-gated, so nothing surfaces and the result is empty.
        var result = await Invoke(handler, new { query = "auth" });

        Assert.DoesNotContain("loose end", result.HumanSummary);
    }

    [Fact]
    public async Task Recall_MissingQuery_ThrowsMissingArgument()
    {
        var handler = NewHandler(out _);

        await Assert.ThrowsAsync<MissingToolArgumentException>(async () =>
            await Invoke(handler, new { limit = 5 }));
    }

    // ─── Valence: recall filter + glyph rendering (ValenceSpec) ───────

    [Fact]
    public async Task Recall_ValenceFilter_MapsToQueryAndReturnsOnlyMatchingStance()
    {
        var handler = NewHandler(out var store);
        store.NextResults =
        [
            Result("memories/r/heuristic/d1", MemoryType.Heuristic, "Npgsql pooling deadlocks under load", "Pooling deadlocks", Valence.Refuting),
            Result("memories/r/heuristic/d2", MemoryType.Heuristic, "In-process cache warmup OOMs the node", "Warmup OOMs the node", Valence.Refuting),
            Result("memories/r/insight/ok", MemoryType.Insight, "Redis is the caching layer", "Redis caching layer", Valence.Affirming),
        ];

        var result = await Invoke(handler, new { query = "cache", valence = "refuting" });

        // Mapping: the string filter reaches the store as MemoryQuery.Valence (the WhereEquals key).
        Assert.NotNull(store.LastQuery);
        Assert.Equal(Valence.Refuting, store.LastQuery!.Valence);

        // Filtering: only the two refuting entries survive; the affirming one is excluded.
        Assert.Equal(2, result.ResultCount);
        Assert.Contains("Pooling deadlocks", result.HumanSummary);
        Assert.Contains("Warmup OOMs the node", result.HumanSummary);
        Assert.DoesNotContain("Redis caching layer", result.HumanSummary);
    }

    [Fact]
    public async Task Recall_RendersValenceGlyphs()
    {
        var handler = NewHandler(out var store);
        store.NextResults =
        [
            Result("memories/r/heuristic/d", MemoryType.Heuristic, "pooling deadlocks under load", "Pooling deadlocks", Valence.Refuting),
            Result("memories/r/heuristic/w", MemoryType.Heuristic, "advisory locks serialize the pool", "Advisory locks serialize", Valence.Cautionary),
            Result("memories/r/insight/ok", MemoryType.Insight, "redis is the cache", "Redis cache", Valence.Affirming),
        ];

        // No valence filter → all three render, each with its stance glyph (or none).
        var result = await Invoke(handler, new { query = "cache" });

        Assert.Equal(3, result.ResultCount);
        Assert.Contains("✗ Pooling deadlocks", result.HumanSummary);       // Refuting
        Assert.Contains("⚠ Advisory locks serialize", result.HumanSummary); // Cautionary
        Assert.Contains("[I] Redis cache", result.HumanSummary);            // Affirming: prefix directly precedes display, no glyph
        Assert.DoesNotContain("✗ Redis cache", result.HumanSummary);
        Assert.DoesNotContain("⚠ Redis cache", result.HumanSummary);
    }

    private static MemoryEntry Result(string id, MemoryType type, string content, string oneLiner, Valence valence) => new()
    {
        Id = id,
        RepoId = "test-repo",
        Type = type,
        Content = content,
        OneLiner = oneLiner,
        Valence = valence,
        Importance = 0.7f,
        CreatedAt = DateTime.UtcNow,
        IsLatest = true,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
    };

    private static RecallToolHandler NewHandler(out RecallStore store)
    {
        store = new RecallStore();
        var svc = new MemoryService(store);
        return new RecallToolHandler(svc);
    }

    private static Task<ToolResult> Invoke(RecallToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_recall",
            "test-repo",
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));

    private sealed class RecallStore : IEidetStore
    {
        public List<MemoryEntry> NextResults { get; set; } = [];

        /// <summary>The last query the lexical arm saw — lets a test assert the
        /// RecallOptions.Valence → MemoryQuery.Valence mapping the Raven store turns into WhereEquals.</summary>
        public MemoryQuery? LastQuery { get; private set; }

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
        public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default) => Task.FromResult(entry.Id);
        public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ForgetAsync(string id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            // Mimic the Raven store's WhereEquals("Valence", …) so a valence-filtered recall returns
            // only matching-stance entries; no filter (null) returns everything.
            var hits = query.Valence is { } v ? NextResults.Where(e => e.Valence == v).ToList() : NextResults;
            return Task.FromResult(hits);
        }
        public Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryEntry>());

        public Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default) =>
            Task.FromResult<MemoryEntry?>(null);
        public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<MemoryType, int>());
        public Task<List<MemoryEntry>> GetTopScoredAsync(string repoId, MemoryType[] types, int limit, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryEntry>());
        public Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default) => Task.FromResult<DatabaseInfo?>(null);
        public Task EnsureIndexesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<MemoryEntry>> BrowseAsync(string repoId, int skip, int take, MemoryType? type = null, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryEntry>());
        public Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default) => Task.FromResult("");
        public Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryLayer>());
        public Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default) =>
            Task.FromResult<MemoryLayer?>(null);
        public Task<List<MemoryEntry>> GetByLayerIdAsync(string layerId, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryEntry>());
        public Task<bool> HardDeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    }
}
