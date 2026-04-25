using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

public class HistoryToolHandlerTests
{
    [Fact]
    public async Task History_NoMatch_ReturnsOkWithEmptyChain()
    {
        var handler = NewHandler(out _);

        var result = await Invoke(handler, new { id = "memories/r/insight/missing" });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal(0, result.ResultCount);
        Assert.Contains("Memory not found", result.HumanSummary);
    }

    [Fact]
    public async Task History_SingleVersion_RendersChain()
    {
        var handler = NewHandler(out var store);
        var entry = new MemoryEntry
        {
            Id = "memories/r/insight/abc",
            RepoId = "r",
            Type = MemoryType.Insight,
            Content = "current version content sufficient for the rendering test",
            Importance = 0.6f,
            CreatedAt = DateTime.UtcNow,
        };
        store.Entries.Add(entry);

        var result = await Invoke(handler, new { id = entry.Id });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal(1, result.ResultCount);
        Assert.Contains("Version history", result.HumanSummary);
        Assert.Contains("(current)", result.HumanSummary);
    }

    [Fact]
    public async Task History_MissingId_ThrowsMissingArgument()
    {
        var handler = NewHandler(out _);

        await Assert.ThrowsAsync<MissingToolArgumentException>(async () =>
            await Invoke(handler, new { }));
    }

    private static HistoryToolHandler NewHandler(out FakeStore store)
    {
        store = new FakeStore();
        var svc = new MemoryService(store);
        return new HistoryToolHandler(svc);
    }

    private static Task<ToolResult> Invoke(HistoryToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_history",
            "test-repo",
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));

    private sealed class FakeStore : IEidetStore
    {
        public List<MemoryEntry> Entries { get; } = [];

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(Entries.FirstOrDefault(e => e.Id == id));
        public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.FromResult(entry.Id); }
        public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ForgetAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryEntry>());
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
