using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Api;
using Eidet.Service.Mcp;
using Raven.Client.Documents;

namespace Eidet.Service;

/// <summary>
/// Shared hosting logic used by both the console ServeCommand and the Windows Service.
/// </summary>
public sealed class EidetHost : IDisposable
{
    private readonly IDocumentStore _store;
    private readonly IEidetStore _eidetStore;
    private readonly EnrichmentService _enrichment;
    private readonly ScheduledTaskService _scheduler;
    private readonly EnrichmentWorker _enrichmentWorker;
    private readonly EidetApiServer _apiServer;
    private HealthMonitor? _healthMonitor;

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

    private EidetHost(IDocumentStore store, IEidetStore eidetStore, EnrichmentService enrichment,
        ScheduledTaskService scheduler, EnrichmentWorker enrichmentWorker,
        EidetApiServer apiServer, EidetConfig config,
        string bind, int port)
    {
        _store = store;
        _eidetStore = eidetStore;
        _enrichment = enrichment;
        _scheduler = scheduler;
        _enrichmentWorker = enrichmentWorker;
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

        // Always deploy indexes on startup — idempotent, updates changed definitions
        DatabaseProvisioner.DeployIndexes(store);
        DatabaseProvisioner.EnsureRefreshEnabled(store);

        var eidetStore = new RavenEidetStore(store);

        var enrichment = config.Enrichment.OllamaEnabled
            ? EnrichmentService.CreateOllama(config.Enrichment.OllamaUrl, config.Enrichment.OllamaModel)
            : EnrichmentService.CreateNull();

        var layerSvc = new LayerService(eidetStore);
        IHookRunner hookRunner = config.Hooks.PreStore.Count > 0 || config.Hooks.PostStore.Count > 0
            || config.Hooks.PreRecall.Count > 0 || config.Hooks.PostRecall.Count > 0
            || config.Hooks.PreForget.Count > 0 || config.Hooks.PostForget.Count > 0
            ? new HookRunner(config.Hooks) : NullHookRunner.Instance;

        var memorySvc = new MemoryService(eidetStore, layerSvc, hookRunner);
        var intakeSvc = new IntakeService(eidetStore);
        var consolidationEngine = new ConsolidationEngine(eidetStore, enrichment);
        IMaintenanceRunner maintenanceRunner = new MaintenanceRunner(
            new MaintenanceOrchestrator(eidetStore, enrichment, consolidationEngine));
        var exportSvc = new ExportService(eidetStore);
        var qualitySvc = new QualityService(eidetStore);
        var usageTracker = new UsageTracker(store);
        var layerSyncSvc = new LayerSyncService(eidetStore, layerSvc);
        var mcpServer = new McpServer(memorySvc, intakeSvc, consolidationEngine, maintenanceRunner,
            Directory.GetCurrentDirectory(), autoIntake: config.Memory.AutoIntakeOnFirstSession, usage: usageTracker,
            export: exportSvc, layers: layerSvc);
        var scheduler = new ScheduledTaskService(store, eidetStore, memorySvc, maintenanceRunner, consolidationEngine, config.Maintenance);
        var apiServer = new EidetApiServer(memorySvc, intakeSvc, consolidationEngine, maintenanceRunner, exportSvc,
            actualBind, actualPort, layerSvc, layerSyncSvc, mcpServer, config.Auth, qualitySvc, enrichment, config, usageTracker, scheduler);
        var enrichmentWorker = new EnrichmentWorker(store, enrichment);

        return new EidetHost(store, eidetStore, enrichment, scheduler, enrichmentWorker, apiServer, config, actualBind, actualPort);
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

    public Task StartSchedulerAsync(CancellationToken ct = default) => _scheduler.StartAsync(ct);

    public Task<List<ScheduledTask>> GetScheduledTasksAsync(CancellationToken ct = default) => _scheduler.GetTasksAsync(ct);

    public Task StartEnrichmentWorkerAsync(CancellationToken ct) => _enrichmentWorker.StartAsync(ct);

    /// <summary>
    /// Starts a background health monitor that checks RavenDB and Ollama every 30 seconds
    /// and fires OnStatusChanged when a dependency's health state changes.
    /// </summary>
    public HealthMonitor StartHealthMonitor(CancellationToken ct)
    {
        _healthMonitor = new HealthMonitor(
            _eidetStore,
            OllamaEnabled,
            OllamaModel,
            OllamaUrl,
            RavenUrl,
            OllamaHealthy,
            ct);
        _healthMonitor.Start();
        return _healthMonitor;
    }

    public Task RunAsync(CancellationToken ct) => _apiServer.RunAsync(ct);

    public void Dispose()
    {
        _healthMonitor?.Dispose();
        _enrichmentWorker.Dispose();
        _scheduler.Dispose();
        _enrichment.Dispose();
        _store.Dispose();
    }
}
