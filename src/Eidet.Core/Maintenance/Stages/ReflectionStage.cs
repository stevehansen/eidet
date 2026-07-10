namespace Eidet.Core.Maintenance.Stages;

/// <summary>
/// Thin delegator to <see cref="ReflectionEngine"/> (mirrors <see cref="ConsolidationStage"/>). Runs
/// LAST in the pipeline so it reflects on the corpus after decay/dedup/consolidation have settled.
/// No-ops unless reflection is explicitly enabled, an enrichment backend is reachable, and the repo is
/// active — the feature ships dormant, so a default run touches nothing.
/// </summary>
internal sealed class ReflectionStage : IMaintenanceStage
{
    public const string StageName = "Reflection";
    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        if (!ctx.Reflection.Config.Enabled || !ctx.Enrichment.IsAvailable || !ctx.IsRepoActive)
            return new StageOutcome(Name, 0);

        var result = await ctx.Reflection.ReflectAsync(
            ctx.RepoId, dryRun: false, ReflectionSource.All, ct, write: ctx.Write);
        return new StageOutcome(Name, result.MemoriesCreated);
    }
}
