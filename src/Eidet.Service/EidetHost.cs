using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Api;
using Eidet.Service.Mcp;
using Eidet.Service.Scheduler;
using Raven.Client.Documents;

namespace Eidet.Service;

/// <summary>
/// Shared hosting logic used by both the console ServeCommand and the Windows Service.
/// </summary>
public sealed class EidetHost : IDisposable
{
    private readonly IDocumentStore _store;
    private readonly IEnrichmentService _enrichment;
    private readonly MaintenanceScheduler _scheduler;
    private readonly EidetApiServer _apiServer;

    public string BindAddress { get; }
    public int Port { get; }
    public StorageMode StorageMode { get; }
    public bool OllamaEnabled { get; }
    public bool OllamaHealthy { get; private set; }
    public string OllamaModel { get; }
    public bool AuthEnabled { get; }
    public int ApiKeyCount { get; }
    public int MaintenanceIntervalHours { get; }
    public int ConsolidationIntervalHours { get; }
    public string RavenUrl { get; }
    public string OllamaUrl { get; }
    public int HookCount { get; }

    private EidetHost(IDocumentStore store, IEnrichmentService enrichment,
        MaintenanceScheduler scheduler, EidetApiServer apiServer, EidetConfig config,
        string bind, int port)
    {
        _store = store;
        _enrichment = enrichment;
        _scheduler = scheduler;
        _apiServer = apiServer;
        BindAddress = bind;
        Port = port;
        StorageMode = config.Storage.Mode;
        OllamaEnabled = config.Enrichment.OllamaEnabled;
        OllamaModel = config.Enrichment.OllamaModel;
        AuthEnabled = config.Auth.Enabled;
        ApiKeyCount = config.Auth.ApiKeys.Count;
        MaintenanceIntervalHours = config.Maintenance.IntervalHours;
        ConsolidationIntervalHours = config.Maintenance.ConsolidationIntervalHours;
        RavenUrl = config.Storage.Mode == StorageMode.Embedded
            ? $"Embedded ({config.Storage.DataDir ?? "default"})"
            : config.Storage.RavenUrl;
        OllamaUrl = config.Enrichment.OllamaUrl;
        HookCount = config.Hooks.PreStore.Count + config.Hooks.PostStore.Count
            + config.Hooks.PreRecall.Count + config.Hooks.PostRecall.Count
            + config.Hooks.PreForget.Count + config.Hooks.PostForget.Count;
    }

    public static EidetHost Create(string? bindAddress = null, int? port = null)
    {
        var config = ConfigManager.Load();
        var actualPort = port ?? config.Service.Port;
        var actualBind = bindAddress ?? config.Service.BindAddress;

        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store);

        IEnrichmentService enrichment = config.Enrichment.OllamaEnabled
            ? new OllamaEnrichmentService(config.Enrichment.OllamaUrl, config.Enrichment.OllamaModel)
            : NullEnrichmentService.Instance;

        var layerSvc = new LayerService(eidetStore);
        IHookRunner hookRunner = config.Hooks.PreStore.Count > 0 || config.Hooks.PostStore.Count > 0
            || config.Hooks.PreRecall.Count > 0 || config.Hooks.PostRecall.Count > 0
            || config.Hooks.PreForget.Count > 0 || config.Hooks.PostForget.Count > 0
            ? new HookRunner(config.Hooks) : NullHookRunner.Instance;

        var memorySvc = new MemoryService(eidetStore, layerSvc, hookRunner);
        var intakeSvc = new IntakeService(eidetStore);
        var consolidationSvc = new ConsolidationService(eidetStore, enrichment);
        var maintenanceSvc = new MaintenanceService(eidetStore, consolidationSvc, enrichment);
        var exportSvc = new ExportService(eidetStore);
        var qualitySvc = new QualityService(eidetStore);
        var mcpServer = new McpServer(memorySvc, intakeSvc, consolidationSvc, maintenanceSvc, exportSvc,
            Directory.GetCurrentDirectory(), autoIntake: config.Memory.AutoIntakeOnFirstSession);
        var apiServer = new EidetApiServer(memorySvc, intakeSvc, consolidationSvc, maintenanceSvc, exportSvc,
            actualBind, actualPort, layerSvc, mcpServer, config.Auth, qualitySvc);

        var scheduler = new MaintenanceScheduler(eidetStore, memorySvc, maintenanceSvc, consolidationSvc, config.Maintenance);

        return new EidetHost(store, enrichment, scheduler, apiServer, config, actualBind, actualPort);
    }

    public async Task<bool> CheckOllamaAsync(CancellationToken ct = default)
    {
        if (!OllamaEnabled) return false;
        OllamaHealthy = await _enrichment.CheckHealthAsync(ct);
        return OllamaHealthy;
    }

    public bool CheckAuthGuard()
    {
        if (AuthEnabled) return true;
        if (BindAddress is "127.0.0.1" or "localhost") return true;
        // Non-localhost without auth — blocked
        return false;
    }

    public void StartScheduler() => _scheduler.Start();

    public Task RunAsync(CancellationToken ct) => _apiServer.RunAsync(ct);

    public void Dispose()
    {
        _scheduler.Dispose();
        if (_enrichment is IDisposable d) d.Dispose();
        _store.Dispose();
    }
}
