using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

public class IntakeToolHandlerTests
{
    [Fact]
    public async Task Intake_ScopedPath_NotFound_ReturnsNotFound()
    {
        var handler = NewHandler();

        var result = await Invoke(handler, new
        {
            path = "C:/this/directory/should/not/exist/at/all/12345abcde",
        });

        Assert.Equal(ToolStatus.NotFound, result.Status);
        Assert.Contains("Directory not found", result.HumanSummary);
    }

    [Fact]
    public async Task Intake_RepoWide_TempDir_ReturnsOk()
    {
        var handler = NewHandler();
        var temp = Directory.CreateTempSubdirectory("eidet-intake-test-");

        try
        {
            var result = await InvokeWithRepo(handler, temp.FullName, new { });

            Assert.Equal(ToolStatus.Ok, result.Status);
            Assert.NotNull(result.Payload);
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }

    private static IntakeToolHandler NewHandler() =>
        new(new IntakeService(new EmptyStore()));

    private static Task<ToolResult> Invoke(IntakeToolHandler handler, object args) =>
        InvokeWithRepo(handler, "test-repo", args);

    private static Task<ToolResult> InvokeWithRepo(IntakeToolHandler handler, string repo, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_intake",
            repo,
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));

    private sealed class EmptyStore : IEidetStore
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
