using Eidet.Core.Domain;
using Eidet.Core.Intake;
using Eidet.Core.Text;

namespace Eidet.Core.Maintenance.Stages;

/// <summary>
/// Repairs corpus damage that predates the write-path fixes, and keeps repairing it for any surface
/// that still slips something through.
///
/// Every repair here is IDEMPOTENT and converges: a clean repo makes this stage a no-op, so it works
/// as a migration for existing installs and as standing hygiene afterwards without a version flag or
/// a one-shot marker to keep track of. That matters because the damage it undoes was produced slowly
/// by scheduled jobs — a corpus that was never repaired looks exactly like one repaired long ago and
/// then re-damaged, and both want the same action.
///
/// Runs BEFORE dedup in the pipeline: folding exact duplicates first shrinks the candidate set the
/// similarity passes have to consider, and re-baselining importance first stops a stale seed score
/// from deciding which of two duplicates survives.
/// </summary>
internal sealed class CorpusRepairStage : IMaintenanceStage
{
    public const string StageName = "CorpusRepair";
    public string Name => StageName;

    /// <summary>
    /// Ceiling for intake-seeded memories. A doc chunk is an unverified restatement of a file the
    /// agent can already open; it must not outrank something an agent actually learned. Older builds
    /// minted these at 0.8 — above the observed AgentInferred median — which inverted the wake-up
    /// ranking, so existing seeds are pulled down to match today's extractors.
    /// </summary>
    private const float MaxIntakeImportance = 0.5f;

    /// <summary>Scan width. Matches the other whole-corpus stages (OrphanCleanup, Deprecate).</summary>
    private const int ScanLimit = 500;

    /// <summary><see cref="MemoryEntry.Source"/> stamped by consolidation. Matched on Source rather than
    /// Provenance because the anti-laundering rule stamps the least-trusted contributor's provenance, so
    /// consolidation output often does not carry <see cref="MemoryProvenance.Consolidation"/> at all.</summary>
    private const string ConsolidationSource = "consolidation";

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var entries = await ctx.Store.GetTopScoredAsync(ctx.RepoId, Enum.GetValues<MemoryType>(), ScanLimit, ct);
        var repaired = 0;

        // Exact-content duplicates, deliberately NOT scoped by type. The similarity dedup engine is
        // type-scoped on purpose (an Observation and an Insight are different claims about the same
        // fact), but byte-identical content across types is never that distinction — it is the
        // signature of a generator that re-emitted its own input. Keep the oldest: it owns the
        // lineage that DerivedFrom edges already point at.
        var byContent = entries
            .Where(e => e.Validity.ValidUntil is null && !string.IsNullOrWhiteSpace(e.Content))
            .GroupBy(e => e.Content.Trim(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in byContent)
        {
            var ordered = group.OrderBy(e => e.CreatedAt).ToList();
            var keep = ordered[0];
            foreach (var discard in ordered.Skip(1))
            {
                // Access counts merge rather than vanish, matching DedupEngine — but importance does
                // NOT, because these copies are generator artifacts rather than independent evidence,
                // and summing their weight is what let one cluster dominate the wake-up slice.
                keep.AccessCount += discard.AccessCount;
                keep.EchoCount += discard.EchoCount;
                keep.FizzleCount += discard.FizzleCount;

                discard.Validity.ValidUntil = ctx.Now;
                discard.ForgetReason = $"Corpus repair: exact-content duplicate of {keep.Id}";
                await ctx.Write.WriteAsync(discard, ct);
                folded.Add(discard.Id);
                repaired++;
            }
            await ctx.Write.WriteAsync(keep, ct);
        }

        repaired += await FoldLineageDuplicatesAsync(ctx, entries, folded, ct);
        repaired += await RetireBodylessIntakeAsync(ctx, entries, folded, ct);

        foreach (var entry in entries)
        {
            if (folded.Contains(entry.Id)) continue;

            var dirty = false;

            var cleanTags = TagHygiene.Clean(entry.Tags);
            if (!cleanTags.SequenceEqual(entry.Tags, StringComparer.Ordinal))
            {
                entry.Tags = cleanTags;
                dirty = true;
            }

            // Same treatment for entities, and for the same reason: they are derived retrieval keys, so
            // re-deriving them is repair rather than an edit to what the memory says. This is what clears
            // the extractor's old chain-of-thought leak — bounded to one week in April on the corpus that
            // exposed it, but a scan cannot tell a never-repaired install from a re-damaged one.
            var cleanEntities = EntityHygiene.Clean(entry.Entities);
            if (!cleanEntities.SequenceEqual(entry.Entities, StringComparer.Ordinal))
            {
                entry.Entities = cleanEntities;
                dirty = true;
            }

            if (entry.Provenance == MemoryProvenance.Intake && entry.Importance > MaxIntakeImportance)
            {
                entry.Importance = MaxIntakeImportance;
                dirty = true;
            }

            // A one-liner that is just the section heading it was mined from ("# CLAUDE.md",
            // "## Architecture") is strictly worse than the fields behind it, and the read path
            // prefers whatever is present. Clearing it lets the render fall through to real content;
            // enrichment treats null as "not yet summarized" and may replace it with something real.
            if (entry.OneLiner is { } ol && IsMinedHeadingOneLiner(ol))
            {
                entry.OneLiner = null;
                dirty = true;
            }

            if (!dirty) continue;
            await ctx.Write.WriteAsync(entry, ct);
            repaired++;
        }

        return new StageOutcome(Name, repaired);
    }

    /// <summary>
    /// Retires consolidation output that re-derives a cluster another live memory already covers,
    /// identified by an identical <see cref="MemoryEntry.DerivedFrom"/> set.
    ///
    /// The exact-content fold above cannot see these. Consolidation re-states its cluster in new words
    /// every run, so the copies are paraphrases: measured on a real corpus, 2,962 of 3,303 live
    /// consolidated memories (90%) sat in an identical-lineage group, 3,303 of them spanning only 450
    /// distinct clusters, with 264 in one repo derived from one identical observation set. No content
    /// comparison finds that — word overlap between two paraphrases of one claim runs about 0.25 — but
    /// lineage states it exactly, and it is lineage the engine itself wrote.
    ///
    /// Scoped to consolidation's own output. Every other writer of <c>DerivedFrom</c> means something
    /// different by a repeated citation: a Canon page cites its approved members, and the two-altitude
    /// procedure path deliberately emits a fine procedure and an abstraction over the same cluster.
    ///
    /// Keeps the oldest, matching the exact-content fold — it owns the lineage that existing
    /// <c>DerivedFrom</c> edges and <c>MemoryLink</c>s already point at, and a stable, content-blind
    /// tiebreak is what lets a second run over the same corpus be a no-op.
    /// </summary>
    private static async Task<int> FoldLineageDuplicatesAsync(
        MaintenanceContext ctx, IReadOnlyList<MemoryEntry> entries, HashSet<string> folded, CancellationToken ct)
    {
        var groups = entries
            .Where(e => e.Validity.ValidUntil is null
                        && !folded.Contains(e.Id)
                        && e.DerivedFrom.Count > 0
                        && string.Equals(e.Source, ConsolidationSource, StringComparison.OrdinalIgnoreCase))
            .GroupBy(
                e => string.Join('|', e.DerivedFrom.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)),
                StringComparer.Ordinal)
            .Where(g => g.Count() > 1);

        var repaired = 0;
        foreach (var group in groups)
        {
            var ordered = group
                .OrderBy(e => e.CreatedAt)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
            var keep = ordered[0];

            foreach (var discard in ordered.Skip(1))
            {
                keep.AccessCount += discard.AccessCount;
                keep.EchoCount += discard.EchoCount;
                keep.FizzleCount += discard.FizzleCount;

                discard.Validity.ValidUntil = ctx.Now;
                discard.ForgetReason = $"Corpus repair: re-derives the same observation cluster as {keep.Id}";
                await ctx.Write.WriteAsync(discard, ct);
                folded.Add(discard.Id);
                repaired++;
            }
            await ctx.Write.WriteAsync(keep, ct);
        }

        return repaired;
    }

    /// <summary>
    /// Retires intake memories whose content is nothing but headings — a label with no knowledge under
    /// it. <see cref="MarkdownIntake.IsHeadingOnly"/> now rejects these at the gate, so this clears what
    /// earlier builds banked: a field corpus held 1,000 of them across 74 repos, 9% of everything live.
    ///
    /// They are not merely low-value, they are actively misleading. Every rendered form of a body-less
    /// memory has to be invented, and 843 of those 1,000 had an LLM-generated one-liner asserting a
    /// claim the repo never made; because L1 prefers the one-liner, 59 wake-up lines across 26 repos
    /// were fabrications, and one repo spent 8 of its 20 slots on them. Clearing the one-liner is not
    /// enough — the fallback chain then renders the heading itself, which is what the exact-content fold
    /// was already colliding on.
    ///
    /// Retires rather than folds: there is no survivor to merge into, because there was never any
    /// content. Scoped to <see cref="MemoryProvenance.Intake"/>, the only writer that mints these
    /// mechanically, so a deliberately terse hand-authored memory is never touched. Observations and
    /// evidence are untouched either way — this retires labels.
    /// </summary>
    private static async Task<int> RetireBodylessIntakeAsync(
        MaintenanceContext ctx, IReadOnlyList<MemoryEntry> entries, HashSet<string> folded, CancellationToken ct)
    {
        var repaired = 0;
        foreach (var entry in entries)
        {
            if (entry.Validity.ValidUntil is not null || folded.Contains(entry.Id)) continue;
            if (entry.Provenance != MemoryProvenance.Intake) continue;
            if (!MarkdownIntake.IsHeadingOnly(entry.Content)) continue;

            entry.Validity.ValidUntil = ctx.Now;
            entry.ForgetReason = "Corpus repair: heading with no body; every rendered form was invented";
            await ctx.Write.WriteAsync(entry, ct);
            folded.Add(entry.Id);
            repaired++;
        }

        return repaired;
    }

    /// <summary>
    /// True when a one-liner is a bare markdown heading rather than a statement — no verb, no
    /// content beyond the heading text. These come from summarizing a doc section whose body is a
    /// table or a list, leaving the heading as the only prose available to summarize.
    ///
    /// Distinct from <see cref="MarkdownIntake.IsHeadingOnly"/>, which asks whether a whole memory has
    /// any BODY at all: this one asks whether a rendered one-liner is a heading copied verbatim, and a
    /// memory with a perfectly good body can still have one.
    /// </summary>
    private static bool IsMinedHeadingOneLiner(string oneLiner)
    {
        var t = oneLiner.Trim();
        if (t.Length == 0) return true;
        if (!t.StartsWith('#')) return false;
        // "# Title" with nothing after the heading line is heading-only; a heading followed by a
        // real sentence is not.
        return !t.TrimStart('#').Trim().Contains('.');
    }
}
