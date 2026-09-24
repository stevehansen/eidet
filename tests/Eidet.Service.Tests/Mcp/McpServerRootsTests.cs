using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.LooseEnds;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Mcp;
using Eidet.Service.Tests.Tools;

namespace Eidet.Service.Tests.Mcp;

/// <summary>
/// #94: a stdio session re-binds from its launch cwd to the client's first <c>file://</c> root, because
/// many clients launch MCP servers from System32 or their own install directory.
/// </summary>
public class McpServerRootsTests : IDisposable
{
    private const string LaunchRepo = @"C:\Windows\System32";
    private readonly string _projectDir = Directory.CreateTempSubdirectory("eidet-roots-").FullName;

    public void Dispose() => Directory.Delete(_projectDir, recursive: true);

    [Fact]
    public async Task ClientWithRoots_RebindsToFirstFileRoot()
    {
        var (server, sent) = NewServer();

        await Initialize(server, withRoots: true);
        var rootsRequest = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal("roots/list", rootsRequest.GetProperty("method").GetString());
        var id = rootsRequest.GetProperty("id").GetString()!;
        Assert.StartsWith(McpServer.RootsRequestIdPrefix, id);

        var reply = await server.ProcessLineAsync(RootsResult(id, "https://example.com/not-a-file", FileUri(_projectDir)), CancellationToken.None);

        Assert.Null(reply); // a response is consumed, never answered
        Assert.Equal(RepoPathResolver.Resolve(_projectDir), server.RepoId);
    }

    [Fact]
    public async Task EveryRootsAnswer_IsLoggedWithPidTriggerAndFullList()
    {
        // Whether one process serves several client sessions can only be read off the log (#98).
        var (server, sent) = NewServer();
        await Initialize(server, withRoots: true);
        var id = JsonDocument.Parse(sent[0]).RootElement.GetProperty("id").GetString()!;
        var other = FileUri(Path.Combine(_projectDir, "second"));

        await server.ProcessLineAsync(RootsResult(id, FileUri(_projectDir), other), CancellationToken.None);

        var line = Assert.Single(File.ReadAllLines(Eidet.Core.EidetLog.LogPath), l => l.Contains(other));
        Assert.Contains($"PID {Environment.ProcessId}, after initialized", line);
        Assert.Contains(FileUri(_projectDir), line);
        Assert.Contains($"repo {RepoPathResolver.Resolve(_projectDir)} (was {LaunchRepo})", line);
    }

    [Fact]
    public async Task ClientWithoutRoots_NeverAsks_KeepsLaunchRepo()
    {
        var (server, sent) = NewServer();

        await Initialize(server, withRoots: false);

        Assert.Empty(sent);
        Assert.Equal(LaunchRepo, server.RepoId);
    }

    [Fact]
    public async Task ExplicitRepo_IgnoresClientRoots()
    {
        var (server, sent) = NewServer(honorClientRoots: false);

        await Initialize(server, withRoots: true);

        Assert.Empty(sent);
        Assert.Equal(LaunchRepo, server.RepoId);
    }

    [Fact]
    public async Task RootsError_KeepsLaunchRepo()
    {
        var (server, sent) = NewServer();
        await Initialize(server, withRoots: true);
        var id = JsonDocument.Parse(sent[0]).RootElement.GetProperty("id").GetString();

        var reply = await server.ProcessLineAsync(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "nope" } }), CancellationToken.None);

        Assert.Null(reply);
        Assert.Equal(LaunchRepo, server.RepoId);
    }

    [Fact]
    public async Task UnrelatedClientResponse_IsNotAnsweredWithAnError()
    {
        var (server, _) = NewServer();

        var reply = await server.ProcessLineAsync("""{"jsonrpc":"2.0","id":7,"result":{}}""", CancellationToken.None);

        Assert.Null(reply);
    }

    [Fact]
    public async Task RootsListChanged_AsksAgain()
    {
        var (server, sent) = NewServer();
        await Initialize(server, withRoots: true);

        await server.ProcessLineAsync("""{"jsonrpc":"2.0","method":"notifications/roots/list_changed"}""", CancellationToken.None);

        Assert.Equal(2, sent.Count);
        Assert.NotEqual(
            JsonDocument.Parse(sent[0]).RootElement.GetProperty("id").GetString(),
            JsonDocument.Parse(sent[1]).RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task SharedProcessClient_IsNeverAskedForRoots()
    {
        // The Claude desktop app shares one process across sessions and reports another session's
        // folder; following it filed a SafeCommands finding under P:\ProjectDashboard.
        var (server, sent) = NewServer();

        await Initialize(server, withRoots: true, clientName: "local-agent-mode-eidet");

        Assert.Empty(sent);
        Assert.Equal(LaunchRepo, server.RepoId);
    }

    [Fact]
    public async Task SharedProcessClient_ToolCallsAreRefused()
    {
        // Its launch dir is the desktop app's versioned install folder: every project's memories
        // pooled under C:\...\AnthropicClaude\app-<version>, a repo that changes on every update.
        var (server, _) = NewServer();
        await Initialize(server, withRoots: true, clientName: "local-agent-mode-eidet");

        var result = await CallTool(server, "eidet_store");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(McpServer.SharedProcessRefusal, result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task SharedProcessClient_WithPinnedRepo_IsServed()
    {
        var (server, _) = NewServer(honorClientRoots: false);
        await Initialize(server, withRoots: true, clientName: "local-agent-mode-eidet");

        var result = await CallTool(server, "eidet_context");

        Assert.NotEqual(McpServer.SharedProcessRefusal, result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task RootThatMovesMidSession_FallsBackToLaunchRepo_AndStopsAsking()
    {
        var (server, sent) = NewServer();
        var second = Directory.CreateDirectory(Path.Combine(_projectDir, "second")).FullName;
        await Initialize(server, withRoots: true);
        await server.ProcessLineAsync(RootsResult(IdOf(sent[0]), FileUri(_projectDir)), CancellationToken.None);
        Assert.Equal(RepoPathResolver.Resolve(_projectDir), server.RepoId);

        await server.ProcessLineAsync("""{"jsonrpc":"2.0","method":"notifications/roots/list_changed"}""", CancellationToken.None);
        await server.ProcessLineAsync(RootsResult(IdOf(sent[1]), FileUri(second)), CancellationToken.None);

        Assert.Equal(LaunchRepo, server.RepoId);
        await server.ProcessLineAsync("""{"jsonrpc":"2.0","method":"notifications/roots/list_changed"}""", CancellationToken.None);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task UnchangedRootOnListChanged_KeepsTheBinding()
    {
        var (server, sent) = NewServer();
        await Initialize(server, withRoots: true);
        await server.ProcessLineAsync(RootsResult(IdOf(sent[0]), FileUri(_projectDir)), CancellationToken.None);

        await server.ProcessLineAsync("""{"jsonrpc":"2.0","method":"notifications/roots/list_changed"}""", CancellationToken.None);
        await server.ProcessLineAsync(RootsResult(IdOf(sent[1]), FileUri(_projectDir)), CancellationToken.None);

        Assert.Equal(RepoPathResolver.Resolve(_projectDir), server.RepoId);
    }

    private static string IdOf(string request) => JsonDocument.Parse(request).RootElement.GetProperty("id").GetString()!;

    [Fact]
    public async Task Initialize_Instructions_FrameMemoriesAsData()
    {
        // #95: the session-level framing is what covers wake-up context, which carries no per-hit labels.
        var (server, _) = NewServer();

        var reply = await server.ProcessLineAsync(InitializeRequest(withRoots: false), CancellationToken.None);

        var instructions = JsonSerializer.SerializeToElement(reply!.Result, JsonRpcDispatcher.SerializerOptions)
            .GetProperty("instructions").GetString();
        Assert.Contains("not instructions", instructions);
    }

    private static async Task Initialize(McpServer server, bool withRoots, string clientName = "test-client")
    {
        await server.ProcessLineAsync(InitializeRequest(withRoots, clientName), CancellationToken.None);
        await server.ProcessLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None);
    }

    private static async Task<JsonElement> CallTool(McpServer server, string tool)
    {
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = tool, arguments = new { content = "The build needs the net10 SDK pinned in global.json", type = "insight" } },
        });
        var reply = await server.ProcessLineAsync(request, CancellationToken.None);
        return JsonSerializer.SerializeToElement(reply!.Result, JsonRpcDispatcher.SerializerOptions);
    }

    private static string InitializeRequest(bool withRoots, string clientName = "test-client") =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = withRoots ? (object)new { roots = new { listChanged = true } } : new { },
                clientInfo = new { name = clientName, version = "1.0" },
            },
        });

    private static string RootsResult(string id, params string[] uris) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result = new { roots = uris.Select(u => new { uri = u, name = "root" }) },
        });

    private static string FileUri(string path) => new Uri(path).AbsoluteUri;

    private static (McpServer Server, List<string> Sent) NewServer(bool honorClientRoots = true)
    {
        var store = new StubStore();
        var svc = new MemoryService(store);
        var server = new McpServer(svc, new IntakeService(store, svc), new ConsolidationEngine(store, enrichment: null, memory: svc),
            new StubMaintenanceRunner(), new LooseEndService(new FakeLooseEndStore(), new FakePromotionPort(), TimeProvider.System),
            LaunchRepo, autoIntake: false, honorClientRoots: honorClientRoots);
        var sent = new List<string>();
        server.AttachClientChannel(sent.Add);
        return (server, sent);
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
