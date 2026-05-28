using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tests.Mcp;

public class AutoIntakeOnContextTests
{
    [Fact]
    public async Task NonTriggerTool_DoesNotCheckStore()
    {
        var store = new FakeStore();
        var auto = NewAutoIntake(store, repoId: "test-repo");

        await auto.OnToolCalledAsync("eidet_recall", CancellationToken.None);
        await auto.OnToolCalledAsync("eidet_store", CancellationToken.None);

        Assert.Equal(0, store.CountsCalls);
    }

    [Fact]
    public async Task TriggerTool_FiresExactlyOnce_AcrossMultipleCalls()
    {
        var store = new FakeStore();
        var auto = NewAutoIntake(store, repoId: "test-repo");

        await auto.OnToolCalledAsync("eidet_context", CancellationToken.None);
        await auto.OnToolCalledAsync("eidet_context", CancellationToken.None);
        await auto.OnToolCalledAsync("eidet_context", CancellationToken.None);

        Assert.Equal(1, store.CountsCalls);
    }

    [Fact]
    public async Task SkipsIntake_WhenRepoAlreadyHasMemories()
    {
        var store = new FakeStore
        {
            CountsToReturn = new Dictionary<MemoryType, int> { [MemoryType.Insight] = 3 },
        };
        var auto = NewAutoIntake(store, repoId: "test-repo");

        await auto.OnToolCalledAsync("eidet_context", CancellationToken.None);

        // Counts was checked once, but intake never ran (no StoreAsync calls).
        Assert.Equal(1, store.CountsCalls);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task SwallowsStoreExceptions()
    {
        var store = new FakeStore { ThrowOnCounts = true };
        var auto = NewAutoIntake(store, repoId: "test-repo");

        // Must not throw — auto-intake failures should never break the
        // triggering tool call.
        await auto.OnToolCalledAsync("eidet_context", CancellationToken.None);
        Assert.Equal(1, store.CountsCalls);

        // Done flag is still set after a thrown exception, so a second call
        // is also a no-op.
        await auto.OnToolCalledAsync("eidet_context", CancellationToken.None);
        Assert.Equal(1, store.CountsCalls);
    }

    [Fact]
    public async Task CustomTriggerTool_Honored()
    {
        var store = new FakeStore();
        var svc = new MemoryService(store);
        var intake = new IntakeService(store, svc);
        var auto = new AutoIntakeOnContext(svc, intake, "test-repo", triggerTool: "custom_trigger");

        await auto.OnToolCalledAsync("eidet_context", CancellationToken.None);
        Assert.Equal(0, store.CountsCalls);

        await auto.OnToolCalledAsync("custom_trigger", CancellationToken.None);
        Assert.Equal(1, store.CountsCalls);
    }

    [Fact]
    public async Task RepoIdIsNormalizedBeforeQueryingStore()
    {
        var store = new FakeStore();
        var auto = NewAutoIntake(store, repoId: @"P:\Some\Project");

        await auto.OnToolCalledAsync("eidet_context", CancellationToken.None);

        Assert.NotNull(store.LastCountsRepoId);
        Assert.Equal(RepoIdNormalizer.Normalize(@"P:\Some\Project"), store.LastCountsRepoId);
    }

    private static AutoIntakeOnContext NewAutoIntake(FakeStore store, string repoId)
    {
        var svc = new MemoryService(store);
        var intake = new IntakeService(store, svc);
        return new AutoIntakeOnContext(svc, intake, repoId);
    }

    private sealed class FakeStore : IEidetStore
    {
        public int CountsCalls;
        public bool ThrowOnCounts;
        public string? LastCountsRepoId;
        public Dictionary<MemoryType, int> CountsToReturn = new();
        public List<MemoryEntry> Entries { get; } = new();

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default)
        {
            CountsCalls++;
            LastCountsRepoId = repoId;
            if (ThrowOnCounts) throw new InvalidOperationException("boom");
            return Task.FromResult(CountsToReturn);
        }

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
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
        public Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default) =>
            Task.FromResult<MemoryEntry?>(null);
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
