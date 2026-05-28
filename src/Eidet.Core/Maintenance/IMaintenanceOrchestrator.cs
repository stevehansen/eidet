using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Maintenance;

public interface IMaintenanceOrchestrator
{
    Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken ct = default);
}

public sealed class MaintenanceOrchestrator : IMaintenanceOrchestrator
{
    private readonly IEidetStore _store;
    private readonly EnrichmentService _enrichment;
    private readonly ConsolidationEngine _consolidation;
    private readonly IReadOnlyList<IMaintenanceStage> _stages;
    private readonly MemoryService? _memory;

    public MaintenanceOrchestrator(
        IEidetStore store,
        EnrichmentService? enrichment = null,
        ConsolidationEngine? consolidation = null,
        IReadOnlyList<IMaintenanceStage>? stages = null,
        MemoryService? memory = null)
    {
        _store = store;
        _enrichment = enrichment ?? EnrichmentService.CreateNull();
        _memory = memory;
        // The default consolidation engine shares this orchestrator's MemoryService so recall and
        // consolidation writes hit one cache; the throwaway fallback is only reached when no memory
        // was supplied (CLI one-shots / tests), where no long-lived recall cache exists to keep coherent.
        _consolidation = consolidation ?? new ConsolidationEngine(store, _enrichment, _memory ?? new MemoryService(store));
        _stages = stages ?? DefaultStages();
    }

    public static IReadOnlyList<IMaintenanceStage> DefaultStages() =>
    [
        new TtlExpiryStage(),
        new ObservationRetentionStage(),
        new DedupSweepStage(),
        new ImportanceDecayStage(),
        new OrphanCleanupStage(),
        new EnrichmentCleanupStage(),
        new HeuristicEnrichmentBackfillStage(),
        new OllamaEnrichmentStage(),
        new ConsolidationStage(),
    ];

    public async Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken ct = default)
    {
        var ctx = new MaintenanceContext
        {
            Store = _store,
            Enrichment = _enrichment,
            Consolidation = _consolidation,
            RepoId = request.RepoId,
            IsRepoActive = request.IsRepoActive,
            ObservationRetentionDays = request.ObservationRetentionDays,
        };

        var report = new MaintenanceReport { RepoId = request.RepoId };

        foreach (var stage in _stages)
        {
            if (ct.IsCancellationRequested) break;

            if (request.OnlyStages is { Count: > 0 } only && !only.Contains(stage.Name)) continue;
            if (request.SkipStages is { Count: > 0 } skip && skip.Contains(stage.Name)) continue;

            try
            {
                var outcome = await stage.ExecuteAsync(ctx, ct);
                report.Stages.Add(outcome);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                report.Stages.Add(new StageOutcome(stage.Name, 0, ex.Message));
            }
        }

        // Every maintenance run is single-repo, so one invalidation of request.RepoId covers
        // all direct-writing stages (TTL/retention/orphan/enrichment) plus DedupSweepStage —
        // none of which invalidate on their own. Gated on net writes to avoid needless misses.
        if (report.Stages.Sum(s => s.Affected) > 0)
            _memory?.InvalidateRecallCache(request.RepoId);

        report.CompletedAt = DateTime.UtcNow;
        return report;
    }
}
