using System.Net;
using System.Net.Sockets;
using Eidet.Core.Configuration;
using Eidet.Core.LooseEnds;
using Eidet.Core.LooseEnds.Promotion;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Api;
using Eidet.Service.Mcp;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace Eidet.Integration.Tests.Fixtures;

public class EidetApiFixture : IAsyncLifetime
{
    private static readonly string TempDataDir = Path.Combine(
        Path.GetTempPath(), "eidet-integration-tests", Guid.NewGuid().ToString("N")[..8]);

    private CancellationTokenSource _cts = new();
    private Task? _serverTask;
    private IDocumentStore? _store;

    public HttpClient Client { get; private set; } = null!;
    public string BaseUrl { get; private set; } = null!;
    public string RepoId { get; } = $"test-repo-{Guid.NewGuid():N}"[..24];
    public bool Available { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            var dbName = $"EidetTest_{Guid.NewGuid():N}"[..24];
            _store = DocumentStoreFactory.CreateEmbedded(TempDataDir, dbName);

            // Deploy indexes
            IndexCreation.CreateIndexes(typeof(Eidet.Core.Indexes.Memories_Search).Assembly, _store);

            // Wire services
            var eidetStore = new RavenEidetStore(_store);
            var layerSvc = new LayerService(eidetStore);
            var memorySvc = new MemoryService(eidetStore, layerSvc);
            var intakeSvc = new IntakeService(eidetStore, memory: memorySvc);
            var consolidationEngine = new ConsolidationEngine(eidetStore, enrichment: null, memory: memorySvc);
            var reflectionEngine = new ReflectionEngine(
                eidetStore, enrichment: null, memory: memorySvc, looseEnds: new RavenLooseEndStore(_store));
            IMaintenanceRunner maintenanceRunner = new MaintenanceOrchestrator(
                eidetStore, memorySvc, consolidation: consolidationEngine, reflection: reflectionEngine);
            var exportSvc = new ExportService(eidetStore, memory: memorySvc);
            var qualitySvc = new QualityService(eidetStore);

            var looseEndSvc = new LooseEndService(
                new RavenLooseEndStore(_store), new MemoryServicePromotionAdapter(memorySvc), TimeProvider.System);
            memorySvc.LooseEnds = looseEndSvc;

            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";

            var layerSyncSvc = new LayerSyncService(eidetStore, layerSvc, memory: memorySvc);

            var server = new EidetApiServer(new EidetApiServerOptions
            {
                Memory = memorySvc,
                Intake = intakeSvc,
                Consolidation = consolidationEngine,
                Reflection = reflectionEngine,
                Maintenance = maintenanceRunner,
                Export = exportSvc,
                LooseEnds = looseEndSvc,
                BindAddress = "127.0.0.1",
                Port = port,
                Layers = layerSvc,
                LayerSync = layerSyncSvc,
                Quality = qualitySvc,
                MemoryFiles = new RavenMemoryFileStore(_store),
            });

            _serverTask = server.RunAsync(_cts.Token);
            Client = new HttpClient { BaseAddress = new Uri(BaseUrl) };

            await WaitForHealthy();
            Available = true;
        }
        catch
        {
            Available = false;
        }
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        if (_serverTask != null)
            try { await _serverTask; } catch { }
        Client?.Dispose();
        _store?.Dispose();
    }

    public async Task WaitForIndexesAsync()
    {
        // Wait up to 15 seconds for indexes to process
        for (var i = 0; i < 30; i++)
        {
            var stats = await _store!.Maintenance.SendAsync(
                new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.StaleIndexes.Length == 0) return;
            await Task.Delay(500);
        }
    }

    private async Task WaitForHealthy()
    {
        for (var i = 0; i < 30; i++)
        {
            try
            {
                var res = await Client.GetAsync("/api/health");
                if (res.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(200);
        }
        throw new TimeoutException("API server did not become healthy in time");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
