namespace Eidet.Core.Maintenance.Stages;

internal sealed class ImportanceDecayStage : IMaintenanceStage
{
    public const string StageName = "ImportanceDecay";
    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var updated = await ctx.Consolidation.ApplyImportanceDecayAsync(
            ctx.RepoId, ctx.IsRepoActive, ct, write: ctx.Write, read: ctx.Store);
        return new StageOutcome(Name, updated);
    }
}
