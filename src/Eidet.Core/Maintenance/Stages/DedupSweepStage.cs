namespace Eidet.Core.Maintenance.Stages;

internal sealed class DedupSweepStage : IMaintenanceStage
{
    public const string StageName = "DedupSweep";

    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var engine = new DedupEngine(ctx.Store, ctx.Enrichment);
        var result = await engine.DedupAsync(ctx.RepoId, ct: ct);
        return new StageOutcome(Name, result.MergedCount);
    }
}
