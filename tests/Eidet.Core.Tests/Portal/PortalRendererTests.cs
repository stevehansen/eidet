using Eidet.Core.Domain;
using Eidet.Core.Portal;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Tests.Portal;

public class PortalRendererTests
{
    [Fact]
    public async Task Empty_repo_renders_identity_stub_and_health_only()
    {
        var renderer = NewRenderer();

        var doc = await renderer.RenderAsync("test-repo");

        Assert.Equal("off", doc.Augment);
        Assert.Equal(2, doc.Sections.Count);
        Assert.Equal(["identity", "health"], doc.Sections.Select(s => s.Id).ToArray());
        var identity = doc.Sections.First(s => s.Id == "identity");
        Assert.Contains("No memories yet", identity.Html);
        Assert.Empty(identity.CitedMemoryIds);
    }

    [Fact]
    public async Task Curated_identity_memory_wins_over_top_insights()
    {
        var curated = MakeMemory("memories/r/insight/curated", MemoryType.Insight,
            importance: 0.5f, oneLiner: "Curated identity",
            tags: ["portal:identity"]);
        var topInsight = MakeMemory("memories/r/insight/top", MemoryType.Insight,
            importance: 0.95f, oneLiner: "Most important insight");

        var renderer = NewRenderer(curated, topInsight);

        var doc = await renderer.RenderAsync("test-repo");

        var identity = doc.Sections.First(s => s.Id == "identity");
        Assert.Single(identity.CitedMemoryIds);
        Assert.Equal(curated.Id, identity.CitedMemoryIds[0]);
        Assert.Contains("Curated identity", identity.Html);
        Assert.DoesNotContain("Most important insight", identity.Html);
    }

    [Fact]
    public async Task Top_3_insights_compose_identity_when_no_curated_memory()
    {
        var insights = new[]
        {
            MakeMemory("memories/r/insight/a", MemoryType.Insight, importance: 0.9f, oneLiner: "A"),
            MakeMemory("memories/r/insight/b", MemoryType.Insight, importance: 0.8f, oneLiner: "B"),
            MakeMemory("memories/r/insight/c", MemoryType.Insight, importance: 0.7f, oneLiner: "C"),
            MakeMemory("memories/r/insight/d", MemoryType.Insight, importance: 0.6f, oneLiner: "D"),
        };
        var renderer = NewRenderer(insights);

        var doc = await renderer.RenderAsync("test-repo");

        var identity = doc.Sections.First(s => s.Id == "identity");
        Assert.Equal(["memories/r/insight/a", "memories/r/insight/b", "memories/r/insight/c"],
            identity.CitedMemoryIds.ToArray());
    }

    [Fact]
    public async Task Architecture_groups_by_primary_tag_alphabetically()
    {
        var renderer = NewRenderer(
            MakeMemory("memories/r/insight/x", MemoryType.Insight, importance: 0.9f, tags: ["zeta", "alpha"]),
            MakeMemory("memories/r/insight/y", MemoryType.Insight, importance: 0.5f, tags: ["beta"]));

        var doc = await renderer.RenderAsync("test-repo");

        var arch = doc.Sections.First(s => s.Id == "architecture");
        // Both memories cited, alpha-tagged group first (importance 0.9 > beta's 0.5).
        var alphaIdx = arch.Html.IndexOf("alpha", StringComparison.Ordinal);
        var betaIdx = arch.Html.IndexOf("beta", StringComparison.Ordinal);
        Assert.True(alphaIdx >= 0 && betaIdx >= 0);
        Assert.True(alphaIdx < betaIdx);
    }

    [Fact]
    public async Task Empty_optional_sections_are_omitted()
    {
        var renderer = NewRenderer(
            MakeMemory("memories/r/insight/a", MemoryType.Insight, importance: 0.5f));

        var doc = await renderer.RenderAsync("test-repo");

        var ids = doc.Sections.Select(s => s.Id).ToArray();
        Assert.Contains("identity", ids);
        Assert.Contains("architecture", ids);
        Assert.Contains("health", ids);
        Assert.DoesNotContain("procedures", ids);
        Assert.DoesNotContain("heuristics", ids);
    }

    [Fact]
    public async Task Procedures_and_heuristics_render_when_present()
    {
        var renderer = NewRenderer(
            MakeMemory("memories/r/procedure/p", MemoryType.Procedure, importance: 0.6f, oneLiner: "Run X"),
            MakeMemory("memories/r/heuristic/h", MemoryType.Heuristic, importance: 0.7f, oneLiner: "Avoid Y"));

        var doc = await renderer.RenderAsync("test-repo");

        Assert.Contains(doc.Sections, s => s.Id == "procedures");
        Assert.Contains(doc.Sections, s => s.Id == "heuristics");
    }

    [Fact]
    public async Task Health_section_includes_counts_and_freshness_buckets()
    {
        var young = MakeMemory("memories/r/insight/young", MemoryType.Insight,
            importance: 0.5f, createdAt: DateTime.UtcNow.AddDays(-2));
        var renderer = NewRenderer(young);

        var doc = await renderer.RenderAsync("test-repo");

        var health = doc.Sections.First(s => s.Id == "health");
        Assert.Contains("Total memories", health.Html);
        Assert.Contains("Created last 7 days", health.Html);
        Assert.Contains("Created last 30 days", health.Html);
    }

    [Fact]
    public async Task Citations_target_memory_hash_route()
    {
        var renderer = NewRenderer(
            MakeMemory("memories/r/insight/a", MemoryType.Insight, importance: 0.5f, oneLiner: "fact"));

        var doc = await renderer.RenderAsync("test-repo");

        var arch = doc.Sections.First(s => s.Id == "architecture");
        Assert.Contains("href=\"#memory/", arch.Html);
        Assert.Contains("data-mid=\"memories/r/insight/a\"", arch.Html);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private static PortalRenderer NewRenderer(params MemoryEntry[] entries)
    {
        var store = new FakeStore(entries);
        var svc = new MemoryService(store);
        return new PortalRenderer(svc);
    }

    private static MemoryEntry MakeMemory(
        string id,
        MemoryType type,
        float importance = 0.5f,
        string? oneLiner = null,
        IEnumerable<string>? tags = null,
        DateTime? createdAt = null,
        MemoryProvenance provenance = MemoryProvenance.AgentInferred) =>
        new()
        {
            Id = id,
            RepoId = "test-repo",
            Type = type,
            Importance = importance,
            OneLiner = oneLiner ?? "(no one-liner)",
            Content = oneLiner ?? "(content)",
            Tags = tags?.ToList() ?? [],
            CreatedAt = createdAt ?? DateTime.UtcNow.AddDays(-1),
            Provenance = provenance,
        };

    private sealed class FakeStore : IEidetStore
    {
        private readonly List<MemoryEntry> _entries;

        public FakeStore(IEnumerable<MemoryEntry> entries) => _entries = entries.ToList();

        public Task<List<MemoryEntry>> BrowseAsync(string repoId, int skip, int take, MemoryType? type = null, CancellationToken ct = default)
        {
            var filtered = _entries.AsEnumerable();
            if (type.HasValue) filtered = filtered.Where(e => e.Type == type.Value);
            return Task.FromResult(filtered.Skip(skip).Take(take).ToList());
        }

        public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default) =>
            Task.FromResult(_entries.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count()));

        // Defaults for everything else.
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));
        public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default) => Task.FromResult(entry.Id);
        public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ForgetAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) => Task.FromResult(new List<MemoryEntry>());
        public Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) => Task.FromResult(new List<MemoryEntry>());
        public Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
        public Task<List<MemoryEntry>> GetTopScoredAsync(string repoId, MemoryType[] types, int limit, CancellationToken ct = default) => Task.FromResult(new List<MemoryEntry>());
        public Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default) => Task.FromResult<DatabaseInfo?>(null);
        public Task EnsureIndexesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default) => Task.FromResult("");
        public Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default) => Task.FromResult(new List<MemoryLayer>());
        public Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default) => Task.FromResult<MemoryLayer?>(null);
        public Task<List<MemoryEntry>> GetByLayerIdAsync(string layerId, CancellationToken ct = default) => Task.FromResult(new List<MemoryEntry>());
        public Task<bool> HardDeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    }
}
