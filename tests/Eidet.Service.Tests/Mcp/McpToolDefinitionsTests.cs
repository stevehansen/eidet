using Eidet.Core.Maintenance;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Mcp;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Mcp;

/// <summary>
/// Asserts the schema surface exposed by <c>tools/list</c>: tool count, naming convention,
/// schema shape, and required fields. Source of truth is each handler's <c>Schema</c> property.
/// </summary>
public class McpToolDefinitionsTests
{
    private static readonly List<McpToolDefinition> Tools = AllHandlers().Select(h => h.Schema).ToList();

    [Fact]
    public void All_Returns13Tools()
    {
        Assert.Equal(13, Tools.Count);
    }

    [Fact]
    public void All_NamesUnique()
    {
        var names = Tools.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void All_NamesStartWithEidet()
    {
        Assert.All(Tools, t => Assert.StartsWith("eidet_", t.Name));
    }

    [Fact]
    public void All_HaveDescriptions()
    {
        Assert.All(Tools, t => Assert.False(string.IsNullOrEmpty(t.Description)));
    }

    [Fact]
    public void All_HaveInputSchema()
    {
        Assert.All(Tools, t =>
        {
            Assert.NotNull(t.InputSchema);
            Assert.Equal("object", t.InputSchema["type"]?.ToString());
        });
    }

    [Theory]
    [InlineData("eidet_store")]
    [InlineData("eidet_recall")]
    [InlineData("eidet_context")]
    [InlineData("eidet_forget")]
    [InlineData("eidet_feedback")]
    [InlineData("eidet_history")]
    [InlineData("eidet_intake")]
    [InlineData("eidet_link")]
    [InlineData("eidet_consolidate")]
    [InlineData("eidet_maintenance")]
    [InlineData("eidet_edit")]
    [InlineData("eidet_pack_export")]
    [InlineData("eidet_pack_import")]
    public void All_ContainsTool(string toolName)
    {
        Assert.Contains(Tools, t => t.Name == toolName);
    }

    [Fact]
    public void Store_RequiresContentAndType()
    {
        var required = RequiredFields("eidet_store");
        Assert.Contains("content", required);
        Assert.Contains("type", required);
    }

    [Fact]
    public void Recall_RequiresQuery() => Assert.Contains("query", RequiredFields("eidet_recall"));

    [Fact]
    public void Forget_RequiresId() => Assert.Contains("id", RequiredFields("eidet_forget"));

    [Fact]
    public void Edit_RequiresId() => Assert.Contains("id", RequiredFields("eidet_edit"));

    [Fact]
    public void Edit_HasOptionalFields()
    {
        var props = Tools.First(t => t.Name == "eidet_edit").InputSchema["properties"]!.AsObject();
        Assert.True(props.ContainsKey("content"));
        Assert.True(props.ContainsKey("tags"));
        Assert.True(props.ContainsKey("importance"));
        Assert.True(props.ContainsKey("confidence"));
        Assert.True(props.ContainsKey("type"));
    }

    [Fact]
    public void PackExport_RequiresPackId() => Assert.Contains("pack_id", RequiredFields("eidet_pack_export"));

    [Fact]
    public void PackImport_RequiresPath() => Assert.Contains("path", RequiredFields("eidet_pack_import"));

    private static IEnumerable<string> RequiredFields(string toolName) =>
        Tools.First(t => t.Name == toolName).InputSchema["required"]!.AsArray()
            .Select(r => r!.ToString());

    private static IEnumerable<IToolHandler> AllHandlers()
    {
        var store = new StubStore();
        var svc = new MemoryService(store);
        var consolidation = new ConsolidationEngine(store, enrichment: null, memory: svc);
        var intake = new IntakeService(store, svc);
        var maintenance = new StubMaintenanceRunner();

        return
        [
            new StoreToolHandler(svc),
            new RecallToolHandler(svc),
            new ForgetToolHandler(svc),
            new FeedbackToolHandler(svc),
            new HistoryToolHandler(svc),
            new ContextToolHandler(svc),
            new LinkToolHandler(svc),
            new ConsolidateToolHandler(consolidation),
            new MaintenanceToolHandler(maintenance, svc),
            new EditToolHandler(svc),
            new IntakeToolHandler(intake),
            new PackExportToolHandler(null),
            new PackImportToolHandler(null, null),
        ];
    }

    private sealed class StubMaintenanceRunner : IMaintenanceRunner
    {
        public Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken ct = default) =>
            Task.FromResult(new MaintenanceReport { RepoId = request.RepoId });
    }

    private sealed class StubStore : IEidetStore
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
