using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Intake.Git;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

/// <summary>
/// Handler surface over <c>IngestGitAsync</c> — argument lifting and summary shape. Uses an
/// in-memory git source registered on the intake service, so no subprocess is spawned.
/// </summary>
public class IntakeGitToolHandlerTests
{
    [Fact]
    public async Task IntakeGit_MinesFixCommit_AndReportsCounts()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("a1b2c3", "fix: null deref in RecallScorer when tags empty", files: ["src/RecallScorer.cs"])
            .AddCommit("d4e5f6", "chore: bump dependencies", files: ["Directory.Packages.props"]);
        var handler = NewHandler(git);

        var result = await Invoke(handler, new { });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Contains("1 new", result.HumanSummary);
        Assert.Contains("1 skipped", result.HumanSummary);
        Assert.Contains("chore", result.HumanSummary); // per-commit skip reason surfaced
    }

    [Fact]
    public async Task IntakeGit_DryRun_ReportsPreviewWording()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("a1b2c3", "fix: null deref in RecallScorer when tags empty", files: ["src/RecallScorer.cs"]);
        var handler = NewHandler(git);

        var result = await Invoke(handler, new { dry_run = true });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.StartsWith("Would mine", result.HumanSummary);
    }

    [Fact]
    public async Task IntakeGit_UnavailableSource_ReportsNotARepo()
    {
        var handler = NewHandler(new InMemoryGitHistorySource { IsAvailable = false });

        var result = await Invoke(handler, new { });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Contains("not a git repository", result.HumanSummary);
    }

    private static IntakeGitToolHandler NewHandler(InMemoryGitHistorySource git)
    {
        var store = new FakeStore();
        return new(new IntakeService(store, [new GitHistoryExtractor(git)], new MemoryService(store)));
    }

    private static Task<ToolResult> Invoke(IntakeGitToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_intake_git",
            "test-repo",
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));

    private sealed class FakeStore : IEidetStore
    {
        private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(_entries.GetValueOrDefault(id));
        public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
        {
            _entries[entry.Id] = entry;
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
