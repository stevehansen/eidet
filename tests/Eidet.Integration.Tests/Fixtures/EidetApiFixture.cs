using System.Net;
using System.Net.Sockets;
using Eidet.Core.Configuration;
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
            var intakeSvc = new IntakeService(eidetStore);
            var consolidationSvc = new ConsolidationService(eidetStore);
            var maintenanceSvc = new MaintenanceService(eidetStore, consolidationSvc);
            var exportSvc = new ExportService(eidetStore);
            var qualitySvc = new QualityService(eidetStore);

            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";

            var server = new EidetApiServer(memorySvc, intakeSvc, consolidationSvc,
                maintenanceSvc, exportSvc, "127.0.0.1", port,
                layerSvc, quality: qualitySvc);

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
