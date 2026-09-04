using Eidet.Core.Canon;
using Eidet.Core.Canon.Sources;
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
using Eidet.Service.Update;
using Raven.Client.Documents;

namespace Eidet.Service;

/// <summary>What an enrichment config reload applied, as reported to the caller.</summary>
public sealed record EnrichmentReloadResult(
    bool Enabled, string Provider, string Url, string Model, bool Healthy,
    bool NightlyModelWorkEnabled, string NightlyModelWork);

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
    private readonly MaintenanceOrchestrator _maintenance;
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

    /// <summary>The wall-clock anchor the nightly pass lands on, <c>HH:mm</c> local.</summary>
    public string MaintenanceAtLocalTime { get; }

    /// <summary>
    /// Whether the nightly pass will call the model at all, and what it will spend per repo when it
    /// does. This is the largest recurring cost in a running Eidet — a drift review batch is
    /// <c>NightlyBatch</c> model calls per repo per night — and it had no surface anywhere, so
    /// turning it off looked identical to leaving it on. Captured at startup like the other banner
    /// values: it describes the configuration this process is running, not the file on disk.
    /// </summary>
    public bool NightlyModelWorkEnabled { get; private set; }
    public string NightlyModelWork { get; private set; }
    public string RavenUrl { get; }
    public string EnrichmentUrl { get; private set; }
    public int EnrichmentFallbackCount { get; private set; }
    public bool HooksEnabled { get; }

    private EidetHost(IDocumentStore store, IEidetStore eidetStore, EnrichmentService enrichment,
        ScheduledTaskService scheduler, EnrichmentWorker enrichmentWorker, MaintenanceOrchestrator maintenance,
        EidetApiServer apiServer, EidetConfig config,
        string bind, int port)
    {
        _store = store;
        _eidetStore = eidetStore;
        _enrichment = enrichment;
        _scheduler = scheduler;
        _enrichmentWorker = enrichmentWorker;
        _maintenance = maintenance;
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
        MaintenanceAtLocalTime = config.Maintenance.ScheduledTime.ToString(@"HH\:mm");
        (NightlyModelWorkEnabled, NightlyModelWork) = DescribeNightlyModelWork(config.Enrichment);
        RavenUrl = config.Storage.Mode == StorageMode.Embedded
            ? $"Embedded ({config.Storage.DataDir ?? "default"})"
            : config.Storage.RavenUrl;
        EnrichmentUrl = config.Enrichment.Url;
        EnrichmentFallbackCount = config.Enrichment.Fallbacks.Count;
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

        var eidetStore = new RavenEidetStore(store, config);

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

        // Canon: drafts in their own collection, minted into memories/* only via the gated adapter. The
        // sources own their reads (store, UL.md); the service reads the store only for citation hydration.
        var canonDraftStore = new RavenCanonDraftStore(store);
        var canonMint = new MemoryServiceCanonAdapter(memorySvc, eidetStore);
        var canonSources = new ICanonDraftSource[]
        {
            new EntityAggregationDraftSource(eidetStore),
            new UbiquitousLanguageDraftSource(),
        };
        var canonSvc = new CanonService(canonDraftStore, canonMint, canonSources, eidetStore, TimeProvider.System);

        var intakeSvc = new IntakeService(eidetStore, memory: memorySvc);
        var consolidationEngine = new ConsolidationEngine(eidetStore, enrichment, memory: memorySvc);
        var reflectionEngine = new ReflectionEngine(
            eidetStore, enrichment, memory: memorySvc, looseEnds: looseEndStore, config: config.Enrichment.Reflection);
        // Coalesced so the scheduler's tick and a hand-triggered REST run cannot rewrite one repo
        // at the same time — the second caller rides the first run instead of starting a second.
        var maintenance = new MaintenanceOrchestrator(
            eidetStore, memorySvc, enrichment, consolidationEngine,
            drift: config.Enrichment.DriftReview,
            reflection: reflectionEngine,
            budget: config.Memory.Budget,
            deprecate: config.Memory.Deprecate);
        IMaintenanceRunner maintenanceRunner = new CoalescingMaintenanceRunner(maintenance);
        var exportSvc = new ExportService(eidetStore, memory: memorySvc);
        var integrityAuditor = new IntegrityAuditor(memorySvc, eidetStore);
        var qualitySvc = new QualityService(eidetStore, integrityAuditor);
        var usageTracker = new UsageTracker(store);
        var layerSyncSvc = new LayerSyncService(eidetStore, layerSvc, memory: memorySvc);
        var mcpServer = new McpServer(memorySvc, intakeSvc, consolidationEngine, maintenanceRunner, looseEndSvc,
            Directory.GetCurrentDirectory(), autoIntake: config.Memory.AutoIntakeOnFirstSession, usage: usageTracker,
            export: exportSvc, layers: layerSvc);
        var scheduler = new ScheduledTaskService(store, eidetStore, maintenanceRunner, consolidationEngine,
            config.Maintenance, config.Update, new CliUpdateInstaller());
        var apiServer = new EidetApiServer(new EidetApiServerOptions
        {
            Memory = memorySvc,
            Intake = intakeSvc,
            Consolidation = consolidationEngine,
            Reflection = reflectionEngine,
            Maintenance = maintenanceRunner,
            Export = exportSvc,
            LooseEnds = looseEndSvc,
            Canon = canonSvc,
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

        var host = new EidetHost(store, eidetStore, enrichment, scheduler, enrichmentWorker, maintenance, apiServer, config, actualBind, actualPort);
        apiServer.EnrichmentReloadHandler = host.ReloadEnrichmentAsync;
        return host;
    }

    /// <summary>
    /// Re-reads the Enrichment section of config.json and applies it to the running service:
    /// swaps the enrichment adapter (every consumer shares the facade), retargets the health
    /// monitor's probes, hands the drift-review and reflection settings to the maintenance
    /// pipeline for its next pass, and starts the enrich-on-store worker when enrichment just
    /// became enabled. Only the Enrichment section is reapplied — everything else needs a restart.
    /// </summary>
    public async Task<EnrichmentReloadResult> ReloadEnrichmentAsync(CancellationToken ct = default)
    {
        var fresh = ConfigManager.Load().Enrichment;
        _config.Enrichment = fresh; // shared root config — keeps /api/status truthful
        _enrichment.Reconfigure(fresh);
        _maintenance.Reconfigure(fresh.DriftReview, fresh.Reflection);
        (NightlyModelWorkEnabled, NightlyModelWork) = DescribeNightlyModelWork(fresh);

        EnrichmentEnabled = fresh.Enabled;
        EnrichmentProvider = fresh.Provider;
        EnrichmentModel = fresh.Model;
        EnrichmentUrl = fresh.Url;
        EnrichmentFallbackCount = fresh.Fallbacks.Count;
        EnrichmentHealthy = fresh.Enabled && await _enrichment.CheckHealthAsync(ct);

        if (fresh.Enabled)
            await _enrichmentWorker.StartAsync(ct); // no-op when already running or backend down

        _healthMonitor?.ReconfigureEnrichment(fresh, EnrichmentHealthy);

        return new EnrichmentReloadResult(fresh.Enabled, fresh.Provider.ToString(), fresh.Url, fresh.Model, EnrichmentHealthy,
            NightlyModelWorkEnabled, NightlyModelWork);
    }

    /// <summary>
    /// Renders <see cref="NightlyModelWork"/>. Every stage named here is gated on enrichment being
    /// enabled as well as its own switch, so an unreachable backend is reported as off rather than
    /// as work that will not happen.
    /// </summary>
    internal static (bool Enabled, string Detail) DescribeNightlyModelWork(EnrichmentConfig config)
    {
        if (!config.Enabled) return (false, "enrichment disabled");

        var parts = new List<string>();
        if (config.DriftReview.Enabled)
            parts.Add(config.DriftReview.ReviewIntervalDays > 0
                ? $"drift review ≤{config.DriftReview.NightlyBatch}/repo, re-reviewed every {config.DriftReview.ReviewIntervalDays}d"
                : $"drift review ≤{config.DriftReview.NightlyBatch}/repo, every night");
        if (config.Reflection.Enabled)
            parts.Add($"reflection ≤{config.Reflection.NightlyBatch}/repo");

        return parts.Count == 0
            ? (false, "drift review and reflection both off")
            : (true, string.Join(", ", parts));
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
        _healthMonitor = new HealthMonitor(_eidetStore, _config.Enrichment, RavenUrl, EnrichmentHealthy, ct);
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
