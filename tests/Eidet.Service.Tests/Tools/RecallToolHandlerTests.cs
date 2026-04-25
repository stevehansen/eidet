using System.Text.Json;
using Eidet.Core.Domain;
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
    public async Task Recall_MissingQuery_ThrowsMissingArgument()
    {
        var handler = NewHandler(out _);

        await Assert.ThrowsAsync<MissingToolArgumentException>(async () =>
            await Invoke(handler, new { limit = 5 }));
    }

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

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
        public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default) => Task.FromResult(entry.Id);
        public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ForgetAsync(string id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
            Task.FromResult(NextResults);
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
