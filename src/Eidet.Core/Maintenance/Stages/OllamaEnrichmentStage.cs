using Eidet.Core.Domain;

namespace Eidet.Core.Maintenance.Stages;

internal sealed class OllamaEnrichmentStage : IMaintenanceStage
{
    public const string StageName = "OllamaEnrichment";
    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        if (!ctx.Enrichment.IsAvailable) return new StageOutcome(Name, 0);

        var entries = await ctx.Store.GetTopScoredAsync(ctx.RepoId, Enum.GetValues<MemoryType>(), 200, ct);
        var enriched = 0;

        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested) break;

            if (await ctx.Enrichment.EnrichMemoryAsync(entry, ct))
            {
                await ctx.Write.WriteAsync(entry, ct);
                enriched++;
            }
        }

        return new StageOutcome(Name, enriched);
    }
}
