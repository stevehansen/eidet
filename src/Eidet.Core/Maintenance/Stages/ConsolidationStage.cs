namespace Eidet.Core.Maintenance.Stages;

internal sealed class ConsolidationStage : IMaintenanceStage
{
    public const string StageName = "Consolidation";
    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var result = await ctx.Consolidation.ConsolidateAsync(ctx.RepoId, dryRun: false, ct);
        // Count boosted insights too: both creates and boosts mutate recall-scoring fields,
        // and this Affected count is the orchestrator's gate for invalidating the recall cache.
        return new StageOutcome(Name, result.InsightsCreated + result.InsightsBoosted);
    }
}
