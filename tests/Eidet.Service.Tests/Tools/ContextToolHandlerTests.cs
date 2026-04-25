using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

public class ContextToolHandlerTests
{
    [Fact]
    public async Task Context_EmptyRepo_ReturnsOk()
    {
        var handler = NewHandler();

        var result = await Invoke(handler, new { });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal(1, result.ResultCount);
        Assert.NotNull(result.Payload);
        Assert.NotNull(result.HumanSummary);
    }

    [Fact]
    public async Task Context_HonorsMaxTokensArg()
    {
        var handler = NewHandler();

        // Just verify the arg path doesn't throw and returns Ok.
        var result = await Invoke(handler, new { max_tokens = 200 });

        Assert.Equal(ToolStatus.Ok, result.Status);
    }

    private static ContextToolHandler NewHandler()
    {
        var svc = new MemoryService(new FakeStore());
        return new ContextToolHandler(svc);
    }

    private static Task<ToolResult> Invoke(ContextToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_context",
            "test-repo",
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));

    private sealed class FakeStore : IEidetStore
    {
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
        public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default) => Task.FromResult(entry.Id);
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
