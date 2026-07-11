namespace Eidet.Core.Maintenance.Stages;

/// <summary>
/// The runtime half of the FAMA post-forget guarantee: nightly, drives the integrity auditor over
/// recently forgotten/superseded memories and folds any leak into the maintenance report as an error
/// (so it shows red). Read-only — it never mutates a memory; it verifies that the mutation stages
/// (forget, dedup, consolidation, edit) that ran earlier this pass actually removed content from every
/// read path. Reads nothing but the store, so it runs even when enrichment is offline.
/// </summary>
internal sealed class ForgetIntegrityStage : IMaintenanceStage
{
    public const string StageName = "ForgetIntegrity";
    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var report = await ctx.Auditor.VerifyForgottenAsync(ctx.RepoId, ct);
        if (report.Clean)
            return new StageOutcome(Name, 0);

        // Affected = leak count; Error set so the report renders it red and Failures surfaces it.
        var summary = string.Join("; ", report.Leaks
            .GroupBy(l => l.Path)
            .Select(g => $"{g.Key}×{g.Count()}"));
        return new StageOutcome(Name, report.Leaks.Count, $"{report.Leaks.Count} forget leak(s): {summary}");
    }
}
