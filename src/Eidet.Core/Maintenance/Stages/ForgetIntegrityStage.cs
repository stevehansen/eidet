using Eidet.Core.Domain;
using Eidet.Core.Integrity;
using Eidet.Core.Memory;

namespace Eidet.Core.Maintenance.Stages;

/// <summary>
/// The runtime half of the FAMA post-forget guarantee plus the nightly trust-claim audit: drives the
/// integrity auditor over the recently forgotten/superseded memories AND the live ones, and folds any
/// unresolved finding into the maintenance report as an error (so it shows red). Verifies that the
/// mutation stages (forget, dedup, consolidation, edit) that ran earlier this pass actually removed
/// content from every read path, and that live memories still satisfy the claims made about them —
/// established provenance, content matching its own id commitment, resolvable lineage.
///
/// The one thing it repairs is unestablished provenance, deterministically: a memory whose provenance was
/// never established but whose <c>Source</c> the current build recognizes is relabelled from that source.
/// This grants nothing a write could not already have claimed — the store path derives provenance from the
/// same <see cref="ProvenanceResolver"/> — so the pre-provenance corpus drains over a few nights with no
/// migration script.
///
/// Draining it is why the repair does NOT take its candidates from the audit report alone. The auditor
/// samples the NEWEST memories (bounded verification of the freshest writes), and documents predating the
/// provenance field are by definition the OLDEST — a report-driven repair could never reach the population
/// it exists to fix. So candidates are the union of two sets: what the audit just saw, so the stage's own
/// red/green stays honest about the sample it verified, and an oldest-first backlog query, which is what
/// actually drains the corpus. The query excludes memories whose source this build cannot map, because
/// those are unrepairable and would otherwise hold the head of that queue forever.
///
/// Repair goes through <c>ctx.Write</c> rather than the auditor's raw store so the touched scope is
/// recorded and the recall cache is invalidated; provenance changes recall scoring, so a stale cache would
/// keep serving pre-repair scores. A repaired finding is not an error — reporting a draining corpus as red
/// every night is noise, so only what is left open sets <c>Error</c>.
/// </summary>
internal sealed class ForgetIntegrityStage : IMaintenanceStage
{
    public const string StageName = "ForgetIntegrity";
    public string Name => StageName;

    // Backlog memories pulled per run, on top of whatever the audit reported. One read plus one write
    // each, once a night — sized so a corpus of a few thousand pre-field documents drains in days
    // rather than months, without turning the maintenance pass into a migration.
    private const int BacklogPerRun = 200;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var report = await ctx.Auditor.VerifyAsync(ctx.RepoId, ct);
        var repaired = await RepairProvenanceAsync(ctx, report, ct);

        var open = report.Findings
            .Where(f => !(f.Check == IntegrityCheck.UnknownProvenance && repaired.Contains(f.MemoryId)))
            .ToList();

        if (open.Count == 0)
            return new StageOutcome(Name, repaired.Count);

        // Affected = unresolved finding count; Error set so the report renders it red and Failures surfaces it.
        var summary = string.Join("; ", open
            .GroupBy(f => f.Check)
            .Select(g => $"{g.Key}×{g.Count()}"));
        var repairNote = repaired.Count > 0 ? $" ({repaired.Count} provenance repaired)" : "";
        return new StageOutcome(Name, open.Count, $"{open.Count} integrity finding(s): {summary}{repairNote}");
    }

    private static async Task<HashSet<string>> RepairProvenanceAsync(
        MaintenanceContext ctx, IntegrityReport report, CancellationToken ct)
    {
        var repaired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in await CandidatesAsync(ctx, report, ct))
        {
            if (ct.IsCancellationRequested) break;

            var entry = await ctx.Write.GetAsync(id, ct);
            if (entry is null || entry.Provenance != MemoryProvenance.Unknown) continue;

            var resolved = ProvenanceResolver.FromSource(entry.Source);
            if (resolved == MemoryProvenance.Unknown) continue; // nothing to derive from — stays provisional

            entry.Provenance = resolved;
            await ctx.Write.WriteAsync(entry, ct);
            repaired.Add(id);
        }
        return repaired;
    }

    /// <summary>
    /// Audit findings first (they decide this stage's own pass/fail), then the oldest-first backlog the
    /// audit's newest-first sample cannot reach. Probe failures carry no memory id and are skipped — a
    /// check that did not run implicates nothing to repair.
    /// </summary>
    private static async Task<List<string>> CandidatesAsync(
        MaintenanceContext ctx, IntegrityReport report, CancellationToken ct)
    {
        var candidates = report.Findings
            .Where(f => f.Check == IntegrityCheck.UnknownProvenance && !f.ProbeFailed)
            .Select(f => f.MemoryId)
            .ToList();
        var seen = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);

        var backlog = await ctx.Store.GetUnprovenancedAsync(
            ctx.RepoId, ProvenanceResolver.RecognizedSources, BacklogPerRun, ct);
        candidates.AddRange(backlog.Select(e => e.Id).Where(seen.Add));

        return candidates;
    }
}
