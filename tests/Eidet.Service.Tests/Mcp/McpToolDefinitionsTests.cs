using Eidet.Core.LooseEnds;
using Eidet.Core.Maintenance;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Mcp;
using Eidet.Service.Tools;
using Eidet.Service.Tests.Tools;

namespace Eidet.Service.Tests.Mcp;

/// <summary>
/// Asserts the schema surface exposed by <c>tools/list</c>: which tools are advertised, tool count,
/// naming convention, schema shape, and required fields. Source of truth is each handler's
/// <c>Schema</c> and <c>McpExposed</c> properties.
/// </summary>
public class McpToolDefinitionsTests
{
    private static readonly List<McpToolDefinition> Tools = AllHandlers().Select(h => h.Schema).ToList();
    private static readonly List<McpToolDefinition> Exposed =
        AllHandlers().Where(h => h.McpExposed).Select(h => h.Schema).ToList();

    [Fact]
    public void All_Registers17Handlers()
    {
        Assert.Equal(17, Tools.Count);
    }

    [Fact]
    public void StoreAndContext_Descriptions_MentionCompactionSurvival()
    {
        // #67: the store/context descriptions reinforce store-before-eviction / reload-after-restart.
        var store = Tools.First(t => t.Name == "eidet_store");
        var context = Tools.First(t => t.Name == "eidet_context");
        Assert.Contains("compaction", store.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compaction", context.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exposed_Returns8Tools()
    {
        Assert.Equal(8, Exposed.Count);
    }

    [Theory]
    [InlineData("eidet_park")]
    [InlineData("eidet_resolve")]
    public void Exposed_ContainsLooseEndTool(string toolName)
    {
        Assert.Contains(Exposed, t => t.Name == toolName);
    }

    [Theory]
    [InlineData("eidet_store")]
    [InlineData("eidet_recall")]
    [InlineData("eidet_context")]
    [InlineData("eidet_forget")]
    [InlineData("eidet_feedback")]
    [InlineData("eidet_link")]
    public void Exposed_ContainsCoreTool(string toolName)
    {
        Assert.Contains(Exposed, t => t.Name == toolName);
    }

    [Theory]
    [InlineData("eidet_history")]
    [InlineData("eidet_intake")]
    [InlineData("eidet_intake_git")]
    [InlineData("eidet_intake_claude_memory")]
    [InlineData("eidet_consolidate")]
    [InlineData("eidet_maintenance")]
    [InlineData("eidet_edit")]
    [InlineData("eidet_pack_export")]
    [InlineData("eidet_pack_import")]
    public void Exposed_ExcludesAdvancedTool(string toolName)
    {
        Assert.DoesNotContain(Exposed, t => t.Name == toolName);
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
    [InlineData("eidet_park")]
    [InlineData("eidet_resolve")]
    public void All_ContainsTool(string toolName)
    {
        Assert.Contains(Tools, t => t.Name == toolName);
    }

    [Fact]
    public void Store_RequiresOnlyContent()
    {
        // Valence sugar: `type` is dropped from `required` (only `content` stays required) so
        // `{ content, negative: true }` is a legal one-line dead-end call. Type is inferred
        // (heuristic) when a non-neutral valence is set.
        var required = RequiredFields("eidet_store");
        Assert.Contains("content", required);
        Assert.DoesNotContain("type", required);
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
        var looseEnds = new LooseEndService(new FakeLooseEndStore(), new FakePromotionPort(), TimeProvider.System);

        // Drive off the real factory so this test tracks the shipped dispatcher surface and can't
        // silently drift from it (a hand-maintained list previously fell behind park/resolve).
        return ToolDispatcherFactory.Create(svc, intake, consolidation, maintenance, looseEnds).Handlers;
    }

    private sealed class StubMaintenanceRunner : IMaintenanceRunner
    {
        public Task<MaintenanceReport> RunAsync(string repoPathOrId, CancellationToken ct = default) =>
            Task.FromResult(new MaintenanceReport { RepoId = repoPathOrId });

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
