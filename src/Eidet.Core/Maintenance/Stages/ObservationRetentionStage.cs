using Eidet.Core.Domain;

namespace Eidet.Core.Maintenance.Stages;

internal sealed class ObservationRetentionStage : IMaintenanceStage
{
    public const string StageName = "ObservationRetention";
    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var observations = await ctx.Store.GetTopScoredAsync(ctx.RepoId, [MemoryType.Observation], 500, ct);
        var cutoff = ctx.Now.AddDays(-ctx.ObservationRetentionDays);
        var graceWindow = ctx.ObservationRetentionDays / 2;
        var expired = 0;

        foreach (var obs in observations.Where(o => o.CreatedAt < cutoff))
        {
            var lastTouched = obs.LastAccessedAt ?? obs.CreatedAt;
            if ((ctx.Now - lastTouched).TotalDays < graceWindow) continue;

            obs.Validity.ValidUntil = ctx.Now;
            obs.ForgetReason = $"Observation retention ({ctx.ObservationRetentionDays}d)";
            await ctx.Store.UpdateAsync(obs, ct);
            expired++;
        }

        return new StageOutcome(Name, expired);
    }
}
