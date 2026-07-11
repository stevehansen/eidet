using Eidet.Core.Domain;

namespace Eidet.Core.Maintenance.Stages;

/// <summary>
/// Retires terminally-stale procedures (#39). Fires only on the subset RoiDecay (#35) can never reach:
/// a Procedure whose Importance is already FadeMem-floored AND is net-negative (fizzles &gt; echoes)
/// AND has been idle beyond <see cref="Configuration.DeprecateConfig.MinIdleDays"/>. RoiDecay only
/// reversibly scales Importance and never forgets, and the floor gate structurally excludes anything it
/// is still actively demoting — so the two never act on the same entry in the same run. Forgets via
/// forget-with-reason (reversible soft-delete, covered by #37's ForgetIntegrity auditor).
/// Quarantined procedures are skipped (same reason as budget eviction).
/// </summary>
internal sealed class DeprecateStage : IMaintenanceStage
{
    public const string StageName = "Deprecate";
    public string Name => StageName;

    // Guards the floor comparison against float rounding — RoiDecay/Decay clamp to exactly Floor.
    private const float FloorEpsilon = 0.001f;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        if (!ctx.Deprecate.Enabled) return new StageOutcome(Name, 0);

        var deprecated = 0;
        var entries = await ctx.Store.GetTopScoredAsync(ctx.RepoId, [MemoryType.Procedure], 500, ct);
        foreach (var e in entries)
        {
            if (ct.IsCancellationRequested) break;
            if (!(e.IsLatest && e.LayerId == null && e.Validity.IsValidAt(ctx.Now) && e.Quarantine is null))
                continue;
            if (e.Importance > FadeMemCurve.Floor + FloorEpsilon) continue;      // still above the floor — RoiDecay's domain
            if (e.FizzleCount <= e.EchoCount) continue;                          // not net-negative
            var idleDays = (ctx.Now - (e.LastAccessedAt ?? e.CreatedAt)).TotalDays;
            if (idleDays <= ctx.Deprecate.MinIdleDays) continue;

            e.Validity.ValidUntil = ctx.Now;
            e.ForgetReason = "deprecated: stale procedure (floored + idle + net-negative)";
            await ctx.Write.WriteAsync(e, ct);
            deprecated++;
        }

        return new StageOutcome(Name, deprecated);
    }
}
