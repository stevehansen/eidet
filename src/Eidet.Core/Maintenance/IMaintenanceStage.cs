using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Maintenance;

public interface IMaintenanceStage
{
    string Name { get; }
    Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct);
}

public readonly record struct StageOutcome(string Name, int Affected, string? Error = null)
{
    public bool Succeeded => Error is null;
}

public sealed class MaintenanceContext
{
    public required IEidetStore Store { get; init; }
    public required BulkMutationCtx Write { get; init; }
    public required EnrichmentService Enrichment { get; init; }
    public required ConsolidationEngine Consolidation { get; init; }
    public required ReflectionEngine Reflection { get; init; }
    public required DedupEngine Dedup { get; init; }

    public required string RepoId { get; init; }
    public required bool IsRepoActive { get; init; }
    public int ObservationRetentionDays { get; init; } = 90;
    public DriftReviewConfig Drift { get; init; } = new();
    public DateTime Now { get; init; } = DateTime.UtcNow;

    /// <summary>Stage-to-stage scratch area — avoid unless truly needed.</summary>
    public IDictionary<string, object> Items { get; } = new Dictionary<string, object>();

    /// <summary>
    /// Drives a single stage without the orchestrator. Builds the dual-use engines internally so
    /// tests pass only the store + bulk write scope; run-constants default to an active test repo.
    /// The engines run on a throwaway <see cref="MemoryService"/> distinct from the one that opened
    /// <paramref name="write"/>; that is coherent only because delegator stages and the engines write
    /// through the supplied <paramref name="write"/> scope — if an engine ever fell back to its own
    /// (<c>write == null</c>) scope, a ForTest run would invalidate the wrong cache.
    /// </summary>
    public static MaintenanceContext ForTest(
        IEidetStore store, BulkMutationCtx write,
        EnrichmentService? enrichment = null, string repoId = "test-repo")
    {
        var memory = new MemoryService(store);
        var enrich = enrichment ?? EnrichmentService.CreateNull();
        return new MaintenanceContext
        {
            Store = store,
            Write = write,
            Enrichment = enrich,
            Consolidation = new ConsolidationEngine(store, enrich, memory),
            Reflection = new ReflectionEngine(store, enrich, memory),
            Dedup = new DedupEngine(store, memory, enrich),
            RepoId = repoId,
            IsRepoActive = true,
        };
    }
}
