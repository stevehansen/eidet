namespace Eidet.Core.Maintenance.Stages;

internal sealed class DedupSweepStage : IMaintenanceStage
{
    public const string StageName = "DedupSweep";

    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var result = await ctx.Dedup.DedupAsync(ctx.RepoId, new DedupOptions(), dryRun: false, write: ctx.Write, ct: ct);
        return new StageOutcome(Name, result.MergedCount);
    }
}
