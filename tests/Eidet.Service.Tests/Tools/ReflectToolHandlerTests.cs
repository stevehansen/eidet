using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

/// <summary>
/// Contract for the off-MCP <see cref="ReflectToolHandler"/> (REST/CLI only). <c>dry_run</c> previews
/// candidates without writing; <c>source</c> parses case-insensitively across the four residue arms and
/// falls back to <c>all</c> on anything unrecognised (the handler mirrors <c>ToolArgs.GetEnum</c>'s
/// tolerant semantics — an unknown source is not a client error). Mirrors the
/// <see cref="StoreToolHandlerTests"/> / <see cref="ConsolidateToolHandlerTests"/> handler idioms.
/// </summary>
public class ReflectToolHandlerTests
{
    private const string OneInsightJson =
        """[{"content":"Redis connection pooling stays stable under sustained production load across restarts","type":"insight","valence":"neutral","tags":["redis"]}]""";

    [Fact]
    public void Handler_is_off_mcp()
    {
        var handler = NewHandler(new SeededStore(), EnrichmentService.CreateNull());
        Assert.False(handler.McpExposed);
        Assert.Equal("eidet_reflect", handler.Name);
    }

    [Fact]
    public async Task DryRun_returns_candidates_without_writing()
    {
        var store = new SeededStore(Echoed("src1"));
        using var enrichment = Enrichment(OneInsightJson);
        var handler = NewHandler(store, enrichment);

        var result = await Invoke(handler, new { dry_run = true });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal(1, result.ResultCount); // one previewed candidate

        var payload = JsonSerializer.SerializeToElement(result.Payload);
        Assert.True(payload.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, payload.GetProperty("memoriesCreated").GetInt32());
        Assert.Empty(store.Written); // preview must not persist anything
    }

    [Theory]
    [InlineData("echoes", "Echoes")]
    [InlineData("looseends", "LooseEnds")]
    [InlineData("drift", "Drift")]
    [InlineData("all", "All")]
    [InlineData("ECHOES", "Echoes")] // case-insensitive
    [InlineData("garbage", "All")]   // unrecognised → tolerant fallback to All
    [InlineData(null, "All")]        // omitted → default All
    public async Task Source_param_parses_case_insensitively_with_all_fallback(string? source, string expected)
    {
        var handler = NewHandler(new SeededStore(), EnrichmentService.CreateNull());
        object args = source is null ? new { dry_run = true } : new { dry_run = true, source };

        var result = await Invoke(handler, args);

        Assert.Equal(ToolStatus.Ok, result.Status);
        var payload = JsonSerializer.SerializeToElement(result.Payload);
        Assert.Equal(expected, payload.GetProperty("source").GetString());
    }

    private static MemoryEntry Echoed(string idSuffix) => new()
    {
        Id = $"memories/repo/observation/{idSuffix}",
        RepoId = "test-repo",
        Type = MemoryType.Observation,
        Content = $"observation {idSuffix} recorded redis pooling behavior under production load",
        Provenance = MemoryProvenance.AgentInferred,
        EchoCount = 5,
        Importance = 0.6f,
        CreatedAt = DateTime.UtcNow.AddDays(-10),
        LastAccessedAt = DateTime.UtcNow.AddDays(-10),
        Validity = new Validity { ValidFrom = DateTime.UtcNow.AddDays(-10) },
        IsLatest = true,
    };

    private static EnrichmentService Enrichment(string reflectResponse) =>
        new(new InMemoryEnrichmentAdapter { IsAvailable = true }.SetResponse(EnrichmentPrompt.Reflect, reflectResponse));

    private static ReflectToolHandler NewHandler(IEidetStore store, EnrichmentService enrichment) =>
        new(new ReflectionEngine(store, enrichment, new MemoryService(store)));

    private static Task<ToolResult> Invoke(ReflectToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_reflect",
            "test-repo",
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));

    /// <summary>
    /// Store fake that returns a fixed residue corpus from <see cref="GetTopScoredAsync"/> and records
    /// every <see cref="StoreAsync"/> in <see cref="Written"/> so a dry-run's no-write claim is observable.
    /// </summary>
    private sealed class SeededStore(params MemoryEntry[] seed) : IEidetStore
    {
        private readonly List<MemoryEntry> _seed = seed.ToList();
        public List<MemoryEntry> Written { get; } = [];

        public Task<List<MemoryEntry>> GetTopScoredAsync(string repoId, MemoryType[] types, int limit, CancellationToken ct = default) =>
            Task.FromResult(_seed.Where(e => types.Contains(e.Type)).Take(limit).ToList());

        public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
        {
            Written.Add(entry);
            return Task.FromResult(entry.Id);
        }

        public Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default) =>
            Task.FromResult<MemoryEntry?>(null);

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
        public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ForgetAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryEntry>());
        public Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryEntry>());
        public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<MemoryType, int>());
        public Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default) => Task.FromResult<DatabaseInfo?>(null);
        public Task EnsureIndexesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<MemoryEntry>> BrowseAsync(string repoId, int skip, int take, MemoryType? type = null, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryEntry>());
        public Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default) => Task.FromResult("");
        public Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryLayer>());
        public Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default) => Task.FromResult<MemoryLayer?>(null);
        public Task<List<MemoryEntry>> GetByLayerIdAsync(string layerId, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryEntry>());
        public Task<bool> HardDeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    }
}
