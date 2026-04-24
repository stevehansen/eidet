using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Maintenance.Stages;

internal sealed class HeuristicEnrichmentBackfillStage : IMaintenanceStage
{
    public const string StageName = "HeuristicEnrichmentBackfill";
    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var entries = await ctx.Store.GetTopScoredAsync(ctx.RepoId, Enum.GetValues<MemoryType>(), 500, ct);
        var enriched = 0;

        foreach (var entry in entries)
        {
            var changed = false;

            if (entry.Entities.Count == 0 && !string.IsNullOrWhiteSpace(entry.Content))
            {
                entry.Entities = EntityExtractor.Extract(entry.Content);
                if (entry.Entities.Count > 0) changed = true;
            }

            if (string.IsNullOrEmpty(entry.OneLiner) && !string.IsNullOrWhiteSpace(entry.Content))
            {
                entry.OneLiner = EntityExtractor.GenerateHeuristicOneLiner(entry.Content);
                if (!string.IsNullOrEmpty(entry.OneLiner)) changed = true;
            }

            if (changed)
            {
                await ctx.Store.UpdateAsync(entry, ct);
                enriched++;
            }
        }

        return new StageOutcome(Name, enriched);
    }
}
