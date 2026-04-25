using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

public class StoreToolHandlerTests
{
    [Fact]
    public async Task Store_ValidContent_ReturnsOkWithId()
    {
        var handler = NewHandler(out _);

        var result = await Invoke(handler, new
        {
            content = "The auth module uses JWT RS256 with a 10-minute access-token TTL",
            type = "observation",
            tags = new[] { "auth" },
            importance = 0.7,
        });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal(1, result.ResultCount);
        Assert.Contains("Stored:", result.HumanSummary);
        Assert.NotNull(result.Payload);
    }

    [Fact]
    public async Task Store_MissingContent_ThrowsMissingArgument()
    {
        var handler = NewHandler(out _);

        await Assert.ThrowsAsync<MissingToolArgumentException>(async () =>
            await Invoke(handler, new { type = "observation" }));
    }

    [Fact]
    public async Task Store_InvalidType_ReturnsBadRequest()
    {
        var handler = NewHandler(out _);

        var result = await Invoke(handler, new
        {
            content = "Some sufficiently detailed content for the gates to accept here",
            type = "not-a-real-type",
        });

        Assert.Equal(ToolStatus.BadRequest, result.Status);
        Assert.Contains("Invalid type", result.HumanSummary);
    }

    [Fact]
    public async Task Store_DuplicateContent_ReturnsConflictWithDuplicateId()
    {
        var handler = NewHandler(out var store);
        var content = "Redis is the caching layer with a five minute TTL on session keys";

        // First store: succeeds
        var first = await Invoke(handler, new { content, type = "insight" });
        Assert.Equal(ToolStatus.Ok, first.Status);

        // Make the next FindDuplicate return the first entry
        store.NextDuplicate = store.Entries.Single();

        var second = await Invoke(handler, new { content, type = "insight" });
        Assert.Equal(ToolStatus.Conflict, second.Status);
        Assert.NotNull(second.DuplicateId);
        Assert.Contains("Near-duplicate", second.HumanSummary);
    }

    [Fact]
    public async Task Store_SecretContent_ReturnsRejected()
    {
        var handler = NewHandler(out _);

        var result = await Invoke(handler, new
        {
            content = "AWS key is AKIAIOSFODNN7EXAMPLE — please do not check in",
            type = "observation",
        });

        Assert.Equal(ToolStatus.Rejected, result.Status);
        Assert.Contains("Blocked", result.HumanSummary, StringComparison.OrdinalIgnoreCase);
    }

    private static StoreToolHandler NewHandler(out CapturingStore store)
    {
        store = new CapturingStore();
        var svc = new MemoryService(store);
        return new StoreToolHandler(svc);
    }

    private static Task<ToolResult> Invoke(StoreToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_store",
            "test-repo",
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));

    private sealed class CapturingStore : IEidetStore
    {
        public List<MemoryEntry> Entries { get; } = [];
        public MemoryEntry? NextDuplicate { get; set; }

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(Entries.FirstOrDefault(e => e.Id == id));

        public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.FromResult(entry.Id);
        }

        public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ForgetAsync(string id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryEntry>());
        public Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
            Task.FromResult(new List<MemoryEntry>());

        public Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default)
        {
            var dup = NextDuplicate;
            NextDuplicate = null;
            return Task.FromResult(dup);
        }

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
