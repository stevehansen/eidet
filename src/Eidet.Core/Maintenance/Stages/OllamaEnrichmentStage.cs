namespace Eidet.Core.Maintenance.Stages;

internal sealed class OllamaEnrichmentStage : IMaintenanceStage
{
    public const string StageName = "OllamaEnrichment";

    /// <summary>Per-repo, per-run cap on enrichment attempts — bounds the nightly LLM cost.</summary>
    internal const int BatchLimit = 50;

    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        if (!ctx.Enrichment.IsAvailable) return new StageOutcome(Name, 0);

        // The retry net behind the EnrichmentWorker: the worker's subscription acks a doc even
        // when enrichment fails and never re-sends it, so this sweep selects whatever is still
        // unenriched — not the top-scored slice, which silently missed low-scoring docs in
        // repos with more than 200 memories.
        var entries = await ctx.Store.GetUnenrichedAsync(ctx.RepoId, BatchLimit, ct);
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
