using Eidet.Core.Domain;

namespace Eidet.Core.Maintenance.Stages;

internal sealed class TtlExpiryStage : IMaintenanceStage
{
    public const string StageName = "TtlExpiry";
    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var entries = await ctx.Store.GetTopScoredAsync(ctx.RepoId, Enum.GetValues<MemoryType>(), 500, ct);
        var expired = 0;

        foreach (var entry in entries.Where(e => e.ForgetAfter.HasValue && e.ForgetAfter.Value <= ctx.Now))
        {
            entry.Validity.ValidUntil = ctx.Now;
            entry.ForgetReason = "TTL expired";
            await ctx.Store.UpdateAsync(entry, ct);
            expired++;
        }

        return new StageOutcome(Name, expired);
    }
}
