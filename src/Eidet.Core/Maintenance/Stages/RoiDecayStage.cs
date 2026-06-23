using Eidet.Core.Domain;
using Eidet.Core.Memory;

namespace Eidet.Core.Maintenance.Stages;

/// <summary>
/// Reversible, Importance-only demotion of proven net-negative action memories — the FadeMem
/// auto-demote of issue #35. For Procedure/Heuristic that have earned enough feedback and run
/// net-negative (fizzles &gt; echoes), it scales Importance by <see cref="MemoryRoi.Factor"/>
/// (the single source of the penalty). It never forgets and never touches content, so one echo
/// back to parity restores full ROI and the next pass leaves the memory alone.
///
/// Ordered after ImportanceDecay and before Consolidation so a demoted procedure doesn't seed an
/// insight. The two Importance stages compose <i>across</i> nightly runs, not within one: this stage
/// re-reads candidates from the (eventually-consistent) search index, so within a single run it may
/// load the pre-ImportanceDecay Importance and its write supersedes that run's age-decay for the same
/// entry. Both writes are monotonic-downward and converge to the floor, so the ordering is safe.
/// </summary>
internal sealed class RoiDecayStage : IMaintenanceStage
{
    public const string StageName = "RoiDecay";
    public string Name => StageName;

    /// <summary>Minimum echo+fizzle evidence before a memory is eligible — keeps demotion conservative.</summary>
    private const int MinFeedback = 3;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        if (!ctx.IsRepoActive) return new StageOutcome(Name, 0);

        var demoted = 0;
        foreach (var type in new[] { MemoryType.Procedure, MemoryType.Heuristic })
        {
            var entries = await ctx.Store.GetTopScoredAsync(ctx.RepoId, [type], 500, ct);
            foreach (var entry in entries)
            {
                if (!(entry.IsLatest && entry.LayerId == null && entry.Validity.IsValidAt(ctx.Now)))
                    continue;
                if (entry.EchoCount + entry.FizzleCount < MinFeedback) continue;
                if (entry.FizzleCount <= entry.EchoCount) continue; // not net-negative — leave it

                var roi = MemoryRoi.Factor(entry);
                var target = Math.Max(FadeMemCurve.Floor, (float)(entry.Importance * roi));
                if (Math.Abs(target - entry.Importance) / Math.Max(entry.Importance, 0.01f) < 0.01f)
                    continue;

                entry.Importance = target;
                await ctx.Write.WriteAsync(entry, ct);
                demoted++;
            }
        }

        return new StageOutcome(Name, demoted);
    }
}
