using Eidet.Core.Configuration;
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
    private readonly MemoryService _memory;
    private readonly EnrichmentService _enrichment;
    private readonly ConsolidationEngine _consolidation;
    private readonly IReadOnlyList<IMaintenanceStage> _stages;
    private readonly DriftReviewConfig _drift;

    public MaintenanceOrchestrator(
        IEidetStore store,
        MemoryService memory,
        EnrichmentService? enrichment = null,
        ConsolidationEngine? consolidation = null,
        IReadOnlyList<IMaintenanceStage>? stages = null,
        DriftReviewConfig? drift = null)
    {
        _store = store;
        _memory = memory;
        _enrichment = enrichment ?? EnrichmentService.CreateNull();
        // The default consolidation engine shares this orchestrator's MemoryService so recall and
        // consolidation writes hit one cache.
        _consolidation = consolidation ?? new ConsolidationEngine(store, _enrichment, _memory);
        _stages = stages ?? DefaultStages();
        _drift = drift ?? new();
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
        new DriftReviewStage(),
        new ConsolidationStage(),
    ];

    public Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken ct = default) =>
        // One bulk scope per run: every direct-writing stage and both dual-use engines write
        // through `write`, so the touched scopes are invalidated exactly once in the finally.
        _memory.RunBulkAsync(async write =>
        {
            var dedup = new DedupEngine(_store, _memory, _enrichment);
            var report = new MaintenanceReport { RepoId = request.RepoId };

            // Built once per run: every field is run-constant, and stages share one Now and one
            // Items scratch dictionary (the documented stage-to-stage contract).
            var ctx = new MaintenanceContext
            {
                Store = _store,
                Write = write,
                Enrichment = _enrichment,
                Consolidation = _consolidation,
                Dedup = dedup,
                RepoId = request.RepoId,
                IsRepoActive = request.IsRepoActive,
                ObservationRetentionDays = request.ObservationRetentionDays,
                Drift = _drift,
            };

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

            report.CompletedAt = DateTime.UtcNow;
            return report;
        }, new BulkOptions { OperationName = "maintenance" }, ct);
}
