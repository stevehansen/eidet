using Eidet.Core.Domain;

namespace Eidet.Core.Maintenance.Stages;

internal sealed class OrphanCleanupStage : IMaintenanceStage
{
    public const string StageName = "OrphanCleanup";
    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var entries = await ctx.Store.GetTopScoredAsync(ctx.RepoId, Enum.GetValues<MemoryType>(), 500, ct);
        var cleaned = 0;

        foreach (var entry in entries)
        {
            var isOrphan = false;

            if (string.IsNullOrWhiteSpace(entry.Content))
                isOrphan = true;

            if (entry.Source == "system" && entry.Importance <= 0.1f && (ctx.Now - entry.CreatedAt).TotalDays > 30)
                isOrphan = true;

            if (!isOrphan) continue;

            entry.Validity.ValidUntil = ctx.Now;
            entry.ForgetReason = "Orphan cleanup";
            await ctx.Write.WriteAsync(entry, ct);
            cleaned++;
        }

        return new StageOutcome(Name, cleaned);
    }
}
