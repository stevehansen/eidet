namespace Eidet.Core.Maintenance;

/// <summary>
/// The 15 maintenance stages, in pipeline order. Each value's name equals the matching
/// stage's <c>StageName</c> const; the orchestrator maps enum ⇄ name at the selection boundary.
/// </summary>
public enum MaintenanceStep
{
    TtlExpiry,
    ObservationRetention,
    CorpusRepair,
    DedupSweep,
    ImportanceDecay,
    RoiDecay,
    Deprecate,
    BudgetEviction,
    OrphanCleanup,
    EnrichmentCleanup,
    HeuristicEnrichmentBackfill,
    OllamaEnrichment,
    DriftReview,
    Consolidation,
    Reflection,
    ForgetIntegrity,
}

public sealed class MaintenanceRequest
{
    public required string RepoId { get; init; }
    public bool? IsRepoActive { get; init; }
    public int ObservationRetentionDays { get; init; } = 90;
    public ISet<MaintenanceStep>? OnlyStages { get; init; }
    public ISet<MaintenanceStep>? SkipStages { get; init; }
}

public sealed class MaintenanceReport
{
    public required string RepoId { get; init; }
    public List<StageOutcome> Stages { get; } = [];
    public DateTime CompletedAt { get; set; }

    public int AffectedBy(string stageName) =>
        Stages.FirstOrDefault(s => s.Name == stageName).Affected;

    public int AffectedBy(MaintenanceStep step) => AffectedBy(step.ToString());

    public IEnumerable<StageOutcome> Failures => Stages.Where(s => !s.Succeeded);

    public override string ToString()
    {
        var parts = Stages.Select(s => s.Succeeded
            ? $"{s.Name}={s.Affected}"
            : $"{s.Name}=ERROR({s.Error})");
        return $"Maintenance complete: {string.Join(", ", parts)}";
    }
}
