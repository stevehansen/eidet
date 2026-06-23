using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

public class FeedbackToolHandlerTests
{
    [Fact]
    public async Task Echo_ExistingMemory_ReturnsOk()
    {
        var handler = NewHandler(out var store);
        var entry = new MemoryEntry { Id = "memories/r/insight/abc", RepoId = "r", Type = MemoryType.Insight, Content = "x" };
        store.Entries.Add(entry);

        var result = await Invoke(handler, new { id = entry.Id, used = true });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Contains("Echo feedback", result.HumanSummary);
    }

    [Fact]
    public async Task Fizzle_ExistingMemory_ReturnsOk()
    {
        var handler = NewHandler(out var store);
        var entry = new MemoryEntry { Id = "memories/r/insight/abc", RepoId = "r", Type = MemoryType.Insight, Content = "x" };
        store.Entries.Add(entry);

        var result = await Invoke(handler, new { id = entry.Id, used = false });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Contains("Fizzle feedback", result.HumanSummary);
    }

    // ─── snake_case reason → FizzleReason mapping (issue #35) ────────────────
    // The FakeStore's UpdateAsync is a no-op and GetAsync hands back the same instance, so the
    // reason that FeedbackAsync stamped onto LastFizzleReason is observable on the seeded entry.

    [Theory]
    [InlineData("version_drift", FizzleReason.VersionDrift)]
    [InlineData("wrong_context", FizzleReason.WrongContext)]
    [InlineData("incorrect", FizzleReason.Incorrect)]
    [InlineData("other", FizzleReason.Other)]
    public async Task Fizzle_KnownReason_MapsToEnum(string wire, FizzleReason expected)
    {
        var handler = NewHandler(out var store);
        var entry = new MemoryEntry { Id = "memories/r/insight/abc", RepoId = "r", Type = MemoryType.Insight, Content = "x" };
        store.Entries.Add(entry);

        await Invoke(handler, new { id = entry.Id, used = false, reason = wire });

        Assert.Equal(expected, entry.LastFizzleReason);
    }

    [Fact]
    public async Task Fizzle_UnknownReason_FallsBackToOther()
    {
        var handler = NewHandler(out var store);
        var entry = new MemoryEntry { Id = "memories/r/insight/abc", RepoId = "r", Type = MemoryType.Insight, Content = "x" };
        store.Entries.Add(entry);

        await Invoke(handler, new { id = entry.Id, used = false, reason = "garbage-not-a-reason" });

        Assert.Equal(FizzleReason.Other, entry.LastFizzleReason);
    }

    [Fact]
    public async Task Echo_IgnoresReason_LeavesLastFizzleReasonNull()
    {
        var handler = NewHandler(out var store);
        var entry = new MemoryEntry { Id = "memories/r/insight/abc", RepoId = "r", Type = MemoryType.Insight, Content = "x" };
        store.Entries.Add(entry);

        // A reason on an echo is meaningless — it must not reach FeedbackAsync (stays null).
        await Invoke(handler, new { id = entry.Id, used = true, reason = "version_drift" });

        Assert.Null(entry.LastFizzleReason);
    }

    [Fact]
    public async Task Fizzle_NoReasonArgument_LeavesLastFizzleReasonNull()
    {
        var handler = NewHandler(out var store);
        var entry = new MemoryEntry { Id = "memories/r/insight/abc", RepoId = "r", Type = MemoryType.Insight, Content = "x" };
        store.Entries.Add(entry);

        await Invoke(handler, new { id = entry.Id, used = false });

        Assert.Null(entry.LastFizzleReason);
    }

    [Fact]
    public async Task UnknownMemory_ReturnsNotFound()
    {
        var handler = NewHandler(out _);

        var result = await Invoke(handler, new { id = "memories/r/insight/missing", used = true });

        Assert.Equal(ToolStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task MissingUsed_ThrowsMissingArgument()
    {
        var handler = NewHandler(out _);

        await Assert.ThrowsAsync<MissingToolArgumentException>(async () =>
            await Invoke(handler, new { id = "memories/r/insight/abc" }));
    }

    private static FeedbackToolHandler NewHandler(out FakeStore store)
    {
        store = new FakeStore();
        var svc = new MemoryService(store);
        return new FeedbackToolHandler(svc);
    }

    private static Task<ToolResult> Invoke(FeedbackToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_feedback",
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
