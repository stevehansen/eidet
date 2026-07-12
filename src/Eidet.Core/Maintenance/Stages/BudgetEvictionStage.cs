using Eidet.Core.Domain;
using Eidet.Core.Memory;

namespace Eidet.Core.Maintenance.Stages;

/// <summary>
/// Budgeted forgetting (#39): when a per-repo/per-type cap is configured, evicts the lowest-retention
/// memories of each type down to the cap. OFF by default (no config ⇒ no eviction). Eviction is
/// forget-with-reason (reversible soft-delete, same as TTL/dedup), so #37's ForgetIntegrity auditor
/// covers it and a wrongly-evicted memory is restored by clearing ValidUntil/ForgetReason.
/// Quarantined memories are excluded — a quarantined memory must stay recallable long enough to earn
/// the echo that clears it (#37's downrank-never-hide); evicting it under budget pressure would be the
/// cold-start starvation #37 warns against.
/// </summary>
internal sealed class BudgetEvictionStage : IMaintenanceStage
{
    public const string StageName = "BudgetEviction";
    public string Name => StageName;

    private const int ScanCap = 1000; // upper bound on the per-type live scan

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        if (!ctx.Budget.Enabled || ctx.Budget.MaxPerType <= 0) return new StageOutcome(Name, 0);

        var evicted = 0;
        foreach (var type in Enum.GetValues<MemoryType>())
        {
            if (ct.IsCancellationRequested) break;

            var live = (await ctx.Store.GetTopScoredAsync(ctx.RepoId, [type], ScanCap, ct))
                .Where(e => e.IsLatest && e.LayerId == null && e.Validity.IsValidAt(ctx.Now)
                            && e.Quarantine is null)
                // Total, stable eviction order: lowest retention first, ties broken deterministically.
                .OrderBy(e => RetentionScore.Of(e, ctx.Now, ctx.Budget.EchoReinforcement))
                .ThenBy(e => e.CreatedAt.Ticks)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToList();

            foreach (var e in live.Take(Math.Max(0, live.Count - ctx.Budget.MaxPerType)))
            {
                var score = RetentionScore.Of(e, ctx.Now, ctx.Budget.EchoReinforcement);
                e.Validity.ValidUntil = ctx.Now;
                e.ForgetReason = $"budget-eviction: {type} cap {ctx.Budget.MaxPerType}, retention {score:F3}";
                await ctx.Write.WriteAsync(e, ct);
                evicted++;
            }
        }

        return new StageOutcome(Name, evicted);
    }
}
