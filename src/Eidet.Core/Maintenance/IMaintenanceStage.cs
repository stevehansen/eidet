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
    public required EnrichmentService Enrichment { get; init; }
    public required ConsolidationEngine Consolidation { get; init; }

    public required string RepoId { get; init; }
    public required bool IsRepoActive { get; init; }
    public int ObservationRetentionDays { get; init; } = 90;
    public DateTime Now { get; init; } = DateTime.UtcNow;

    /// <summary>Stage-to-stage scratch area — avoid unless truly needed.</summary>
    public IDictionary<string, object> Items { get; } = new Dictionary<string, object>();
}
