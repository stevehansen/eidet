using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Integrity;
using Eidet.Core.LooseEnds;
using Eidet.Core.LooseEnds.Promotion;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Api;
using Eidet.Service.Mcp;
using Raven.Client.Documents;

namespace Eidet.Service;

/// <summary>What an enrichment config reload applied, as reported to the caller.</summary>
public sealed record EnrichmentReloadResult(bool Enabled, string Provider, string Url, string Model, bool Healthy);

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
    private readonly EidetConfig _config;
    private HealthMonitor? _healthMonitor;

    public string BindAddress { get; }
    public int Port { get; }
    public StorageMode StorageMode { get; }
    public bool EnrichmentEnabled { get; private set; }
    public EnrichmentProvider EnrichmentProvider { get; private set; }
    public bool EnrichmentHealthy { get; private set; }
    public string EnrichmentModel { get; private set; }
    public bool AuthEnabled { get; }
    public int ApiKeyCount { get; }
    public int MaintenanceIntervalHours { get; }
    public int ConsolidationIntervalHours { get; }
    public string RavenUrl { get; }
    public string EnrichmentUrl { get; private set; }
    public bool HooksEnabled { get; }

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
        _config = config;
        BindAddress = bind;
        Port = port;
        StorageMode = config.Storage.Mode;
        EnrichmentEnabled = config.Enrichment.Enabled;
        EnrichmentProvider = config.Enrichment.Provider;
        EnrichmentModel = config.Enrichment.Model;
        AuthEnabled = config.Auth.Enabled;
        ApiKeyCount = config.Auth.ApiKeys.Count;
        MaintenanceIntervalHours = config.Maintenance.IntervalHours;
        ConsolidationIntervalHours = config.Maintenance.ConsolidationIntervalHours;
        RavenUrl = config.Storage.Mode == StorageMode.Embedded
            ? $"Embedded ({config.Storage.DataDir ?? "default"})"
            : config.Storage.RavenUrl;
        EnrichmentUrl = config.Enrichment.Url;
        HooksEnabled = config.Hooks.AnyEnabled();
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
        DatabaseProvisioner.EnsureMemoryFileRevisions(store);

        var eidetStore = new RavenEidetStore(store);

        var enrichment = EnrichmentService.CreateFromConfig(config.Enrichment);

        var layerSvc = new LayerService(eidetStore);
        var hookRunner = new HookRunner(config.Hooks);
        var poisonLog = new RavenPoisonLog(store);

        var memorySvc = new MemoryService(eidetStore, layerSvc, hookRunner, poisonLog);

        // Loose Ends wire AFTER memorySvc: the promotion adapter wraps memorySvc, and memorySvc
        // needs looseEndSvc for the wake-up slice — so the slice is a settable property, not a
        // ctor edge, to break that construction cycle.
        var looseEndStore = new RavenLooseEndStore(store);
        var promotion = new MemoryServicePromotionAdapter(memorySvc);
        var looseEndSvc = new LooseEndService(looseEndStore, promotion, TimeProvider.System);
        memorySvc.LooseEnds = looseEndSvc;

        var intakeSvc = new IntakeService(eidetStore, memory: memorySvc);
        var consolidationEngine = new ConsolidationEngine(eidetStore, enrichment, memory: memorySvc);
        var reflectionEngine = new ReflectionEngine(
            eidetStore, enrichment, memory: memorySvc, looseEnds: looseEndStore, config: config.Enrichment.Reflection);
        IMaintenanceRunner maintenanceRunner = new MaintenanceOrchestrator(
            eidetStore, memorySvc, enrichment, consolidationEngine,
            drift: config.Enrichment.DriftReview,
            reflection: reflectionEngine,
            budget: config.Memory.Budget,
            deprecate: config.Memory.Deprecate);
        var exportSvc = new ExportService(eidetStore, memory: memorySvc);
        var integrityAuditor = new IntegrityAuditor(memorySvc, eidetStore);
        var qualitySvc = new QualityService(eidetStore, integrityAuditor);
        var usageTracker = new UsageTracker(store);
        var layerSyncSvc = new LayerSyncService(eidetStore, layerSvc, memory: memorySvc);
        var mcpServer = new McpServer(memorySvc, intakeSvc, consolidationEngine, maintenanceRunner, looseEndSvc,
            Directory.GetCurrentDirectory(), autoIntake: config.Memory.AutoIntakeOnFirstSession, usage: usageTracker,
            export: exportSvc, layers: layerSvc);
        var scheduler = new ScheduledTaskService(store, eidetStore, maintenanceRunner, consolidationEngine, config.Maintenance);
        var apiServer = new EidetApiServer(new EidetApiServerOptions
        {
            Memory = memorySvc,
            Intake = intakeSvc,
            Consolidation = consolidationEngine,
            Reflection = reflectionEngine,
            Maintenance = maintenanceRunner,
            Export = exportSvc,
            LooseEnds = looseEndSvc,
            BindAddress = actualBind,
            Port = actualPort,
            Layers = layerSvc,
            LayerSync = layerSyncSvc,
            Mcp = mcpServer,
            Auth = config.Auth,
            Quality = qualitySvc,
            Enrichment = enrichment,
            Config = config,
            Usage = usageTracker,
            ScheduledTasks = scheduler,
            MemoryFiles = new RavenMemoryFileStore(store),
        });
        var enrichmentWorker = new EnrichmentWorker(store, enrichment, memorySvc);

        var host = new EidetHost(store, eidetStore, enrichment, scheduler, enrichmentWorker, apiServer, config, actualBind, actualPort);
        apiServer.EnrichmentReloadHandler = host.ReloadEnrichmentAsync;
        return host;
    }

    /// <summary>
    /// Re-reads the Enrichment section of config.json and applies it to the running service:
    /// swaps the enrichment adapter (every consumer shares the facade), retargets the health
    /// monitor's probe, and starts the enrich-on-store worker when enrichment just became
    /// enabled. Only the Enrichment section is reapplied — everything else needs a restart.
    /// </summary>
    public async Task<EnrichmentReloadResult> ReloadEnrichmentAsync(CancellationToken ct = default)
    {
        var fresh = ConfigManager.Load().Enrichment;
        _config.Enrichment = fresh; // shared root config — keeps /api/status truthful
        _enrichment.Reconfigure(fresh);

        EnrichmentEnabled = fresh.Enabled;
        EnrichmentProvider = fresh.Provider;
        EnrichmentModel = fresh.Model;
        EnrichmentUrl = fresh.Url;
        EnrichmentHealthy = fresh.Enabled && await _enrichment.CheckHealthAsync(ct);

        if (fresh.Enabled)
            await _enrichmentWorker.StartAsync(ct); // no-op when already running or backend down

        _healthMonitor?.ReconfigureEnrichment(fresh.Enabled, fresh.Provider, fresh.Model, fresh.Url, EnrichmentHealthy);

        return new EnrichmentReloadResult(fresh.Enabled, fresh.Provider.ToString(), fresh.Url, fresh.Model, EnrichmentHealthy);
    }

    public async Task<bool> CheckEnrichmentAsync(CancellationToken ct = default)
    {
        if (!EnrichmentEnabled) return false;
        EnrichmentHealthy = await _enrichment.CheckHealthAsync(ct);
        return EnrichmentHealthy;
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
    /// Starts a background health monitor that checks RavenDB and the enrichment backend every
    /// 30 seconds and fires OnStatusChanged when a dependency's health state changes.
    /// </summary>
    public HealthMonitor StartHealthMonitor(CancellationToken ct)
    {
        _healthMonitor = new HealthMonitor(
            _eidetStore,
            EnrichmentEnabled,
            EnrichmentProvider,
            EnrichmentModel,
            EnrichmentUrl,
            RavenUrl,
            EnrichmentHealthy,
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
