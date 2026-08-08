using Eidet.Core.Canon;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Core.Text;

namespace Eidet.Core.Maintenance;

/// <summary>
/// Consolidation engine: groups observations by tag overlap and either creates new
/// insights or boosts existing ones. Exposed publicly so API / MCP / scheduler can
/// run consolidation in dry-run or stand-alone mode without spinning up the full
/// maintenance pipeline. Also owns per-type FadeMem decay application.
/// </summary>
public sealed class ConsolidationEngine
{
    private readonly IEidetStore _store;
    private readonly EnrichmentService _enrichment;
    private readonly MemoryService _memory;

    public ConsolidationEngine(IEidetStore store, EnrichmentService? enrichment, MemoryService memory)
    {
        _store = store;
        _enrichment = enrichment ?? EnrichmentService.CreateNull();
        _memory = memory;
    }

    public async Task<ConsolidationResult> ConsolidateAsync(
        string repoId, bool dryRun = false, CancellationToken ct = default, BulkMutationCtx? write = null)
    {
        var result = new ConsolidationResult();

        var observations = await _store.GetTopScoredAsync(repoId, [MemoryType.Observation], 200, ct);
        observations = observations
            .Where(o => o.DerivedFrom.Count == 0 && o.Validity.ValidUntil == null)
            .ToList();

        if (observations.Count < 3) return result;

        var groups = TagOverlapGrouper.Group(observations);

        // Join the caller's bulk scope when handed one (maintenance stage); otherwise open our own.
        if (write is { } w)
            await ConsolidateGroupsAsync(w, repoId, groups, dryRun, result, ct);
        else
            await _memory.RunBulkAsync(
                ctx => ConsolidateGroupsAsync(ctx, repoId, groups, dryRun, result, ct),
                new BulkOptions { OperationName = "consolidate" }, ct);

        return result;
    }

    private async Task<int> ConsolidateGroupsAsync(
        BulkMutationCtx ctx, string repoId, IReadOnlyList<List<MemoryEntry>> groups,
        bool dryRun, ConsolidationResult result, CancellationToken ct)
    {
        // Which observations a live derived memory already folds in. This — not a content probe — is
        // the idempotence guard, because it answers the question actually being asked ("did I already
        // consolidate these sources?") with lineage the engine itself wrote, rather than inferring it
        // from similarity. A content probe cannot answer it: an unenriched consolidation emits the
        // representative's content verbatim, so the nearest match to a bucket's output is the bucket's
        // own input, and every scheduled run reads done as not-done and emits another copy.
        var consumed = await ConsumedObservationIdsAsync(repoId, ct);

        // Partition each tag group by valence sign so opposite stances consolidate independently
        // and a contradiction is never collapsed into one insight.
        foreach (var group in groups)
        foreach (var bucket in group.GroupBy(o => ValencePolarity.Sign(o.Valence)).Select(g => g.ToList()))
        {
            if (bucket.Count < 3) continue;

            // Fully-consumed bucket: nothing new to fold. Partially-consumed still runs — fresh
            // evidence joining an old cluster is exactly what the boost path is for.
            if (bucket.All(o => consumed.Contains(o.Id))) continue;

            var bucketValence = bucket.FirstOrDefault(o => o.Valence != Valence.Neutral)?.Valence ?? Valence.Neutral;
            // Ranked + capped, not a raw union: a consolidated memory can itself be re-consolidated,
            // so an uncapped union compounds each generation and tags eventually cover the corpus.
            var unionTags = TagHygiene.Clean(bucket.SelectMany(o => o.Tags));
            var meanImportance = bucket.Average(o => o.Importance);
            var proposedImportance = Math.Min(1.0f, (float)(meanImportance * 1.2));
            var representative = bucket.OrderByDescending(o => o.Importance).First();

            var candidate = new ConsolidationCandidate
            {
                ObservationIds = bucket.Select(o => o.Id).ToList(),
                Tags = unionTags,
                ProposedContent = representative.Content,
                ProposedImportance = proposedImportance,
            };
            result.Candidates.Add(candidate);

            if (dryRun) continue;

            // Two-altitude procedure emission (#39): when the cluster carries a determinable functional
            // stage (#38), it is procedure-shaped — emit a fine-grained steps procedure + a script-like
            // abstraction over it, both stage-tagged (honoring #38's "emitted subtask memories carry
            // Stage != None"). An all-None cluster falls through to today's single-altitude Insight path
            // unchanged — a zero-LLM path can't fabricate a stage.
            var stage = DeterminableStage(bucket);
            if (stage != FunctionalStage.None)
            {
                await EmitTwoAltitudeProceduresAsync(
                    ctx, repoId, bucket, stage, bucketValence, unionTags, proposedImportance, representative, candidate.ObservationIds, result, ct);
                continue;
            }

            // Never boost an insight that takes the opposite hard stance from this bucket — a
            // conflicting bucket falls through to create its own insight instead, so both
            // stances coexist (mirrors the write-path polarity guards).
            // A canon:* page is a human-curated memory; consolidation must never boost or contaminate its
            // lineage — skip the boost and fall through to a fresh insight (mirrors the valence-conflict guard).
            // Probe for an existing consolidated INSIGHT, not merely "some memory with this content".
            // A type-agnostic probe is guaranteed to return one of this bucket's own observations —
            // when enrichment is unavailable the emitted content IS the representative's content, so
            // the source matches at similarity 1.0. Reading that as "nothing consolidated yet" is what
            // made this branch re-emit a verbatim copy every scheduled cycle (240 copies of a single
            // observation observed in the field). FindNearDuplicatesAsync filters on the probe's type,
            // so an Insight-typed probe cannot come back holding an Observation.
            var existingInsight = await FindConsolidatedAsync(repoId, MemoryType.Insight, representative.Content, ct);
            if (existingInsight is not null &&
                !ValencePolarity.Conflicts(existingInsight.Valence, bucketValence) &&
                !CanonTags.IsCanonPage(existingInsight))
            {
                // Anti-laundering (boost path): only trusted sources may lift a trusted insight. An
                // attacker must not be able to raise a good insight's importance — or contaminate its
                // lineage — by injecting low-trust (Pack/Intake) observations that happen to match it.
                // No trusted contributors → skip the boost entirely (a "compression-amplified toxin").
                var trusted = bucket.Where(ProvenanceRules.IsTrusted).ToList();
                if (trusted.Count == 0) continue;

                // Only NEW evidence may boost. The bucket reforms identically every cycle, so boosting
                // on the full trusted set would walk importance to 1.0 on nothing but the passage of
                // time — and importance alone orders the L1 wake-up pool. No new contributors is the
                // steady state, and the steady state must be a no-op.
                var fresh = trusted
                    .Where(o => !existingInsight.DerivedFrom.Contains(o.Id, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (fresh.Count == 0) continue;

                existingInsight.Importance = Math.Min(1.0f, existingInsight.Importance + 0.05f * fresh.Count);
                existingInsight.DerivedFrom = existingInsight.DerivedFrom
                    .Concat(fresh.Select(o => o.Id))
                    .Distinct()
                    .ToList();
                await ctx.WriteAsync(existingInsight, ct);
                result.InsightsBoosted++;
            }
            else
            {
                // Every cluster gets a real merge attempt, not just large ones. The old `> 5` gate
                // decided which clusters were worth an LLM call, but what it actually decided was
                // which clusters got a genuine merge and which got a verbatim copy of their own
                // representative — and the small ones, the overwhelming majority, all got the copy.
                var mergedContent = DeterministicMerge(bucket);
                if (_enrichment.IsAvailable)
                {
                    var merged = await _enrichment.MergeObservationsAsync(
                        bucket.Select(o => o.Content).ToList(), ct);
                    if (!string.IsNullOrEmpty(merged))
                        mergedContent = merged;
                }

                // No enrichment, or a merge that came back as one of the inputs: the cluster has no
                // insight to add today. Emit nothing and leave it for a run that can say something
                // new — an unconsolidated cluster is a cheap no-op, a duplicate is not.
                if (AddsNothing(bucket, mergedContent)) continue;

                // Anti-laundering (create path): if ANY contributing observation is untrusted
                // (Pack/Intake), stamp the new insight with the least-trusted contributor's
                // provenance instead of Consolidation, so MemoryTrust.Factor keeps demoting it at
                // recall. This stops an attacker laundering a poisoned observation into a fully
                // trusted insight ("compression-amplified toxin"). The audit trail — Source and
                // DerivedFrom — is preserved untouched.
                var provenance = ProvenanceRules.ForContributors(bucket);

                var now = DateTime.UtcNow;
                var insight = new MemoryEntry
                {
                    Id = MemoryIdGenerator.Generate(repoId, MemoryType.Insight, mergedContent, now),
                    RepoId = repoId,
                    Type = MemoryType.Insight,
                    Valence = bucketValence,
                    Content = mergedContent,
                    Tags = unionTags,
                    Importance = proposedImportance,
                    Source = "consolidation",
                    Provenance = provenance,
                    Confidence = 0.7f,
                    CreatedAt = now,
                    Validity = new Validity { ValidFrom = now },
                    DerivedFrom = candidate.ObservationIds,
                    Entities = EntityExtractor.Extract(mergedContent),
                    OneLiner = EntityExtractor.GenerateHeuristicOneLiner(mergedContent),
                };
                await ctx.StoreNewAsync(insight, ct);
                result.InsightsCreated++;
            }
        }
        return 0;
    }

    /// <summary>
    /// The already-consolidated memory of <paramref name="type"/> covering <paramref name="content"/>,
    /// or null when this cluster has not been consolidated yet.
    ///
    /// Type-scoped on purpose: consolidation emits content that is often byte-identical to one of its
    /// own sources, so any type-agnostic "does this content exist" probe answers with the source and
    /// makes an already-done cluster look undone. Scoping to the OUTPUT type is what makes the check
    /// mean "did I already emit this", which is the question a scheduled, repeatedly-run stage needs.
    /// </summary>
    private Task<MemoryEntry?> FindConsolidatedAsync(
        string repoId, MemoryType type, string content, CancellationToken ct) =>
        _store.FindDuplicateOfTypeAsync(repoId, type, content, 0.85f, ct);

    /// <summary>
    /// Observation ids already folded into a derived memory (insight or procedure), live or not.
    /// Read once per run: consolidation emits at most a handful of memories per pass, so a snapshot
    /// taken at the start cannot go stale within it, and the alternative — a probe per bucket —
    /// multiplies round trips for an answer that does not change.
    ///
    /// Retired lineage counts. Scoping this to live memories made consolidation and the nightly
    /// repair drive each other in a loop: the deterministic merge emitted the representative's
    /// content verbatim, corpus repair retired that insight as an exact-content duplicate of its own
    /// source observation, the cluster's only lineage record went with it, and the next run minted
    /// another copy. The store answers for the full history; the live scan is unioned in so fakes
    /// that do not implement it keep working.
    /// </summary>
    private async Task<HashSet<string>> ConsumedObservationIdsAsync(string repoId, CancellationToken ct)
    {
        var consumed = await _store.GetConsolidatedSourceIdsAsync(repoId, ct);

        var derived = await _store.GetTopScoredAsync(
            repoId, [MemoryType.Insight, MemoryType.Procedure], ConsumedScanLimit, ct);
        foreach (var d in derived)
        foreach (var id in d.DerivedFrom)
            consumed.Add(id);

        return consumed;
    }

    /// <summary>
    /// True when <paramref name="content"/> is byte-identical to one of the cluster's own sources —
    /// i.e. the "merge" produced nothing the corpus does not already hold.
    ///
    /// This is the condition to refuse, not to repair. Emitting the copy creates an exact-content
    /// duplicate of a memory that already exists, which the nightly repair then retires; the write is
    /// pure churn even in the best case, and it re-enters the corpus as consolidation bait every time
    /// the retirement takes its lineage with it.
    /// </summary>
    private static bool AddsNothing(IReadOnlyList<MemoryEntry> bucket, string content) =>
        bucket.Any(o => string.Equals(o.Content, content, StringComparison.Ordinal));

    /// <summary>
    /// What a cluster consolidates to when no model is available to merge it: the ordered union of
    /// its distinct source contents.
    ///
    /// The zero-LLM path used to nominate the highest-importance member's content as the "merge",
    /// which is a pick, not a merge — the emitted insight was byte-identical to a memory the corpus
    /// already held. The nightly repair retired it as an exact-content duplicate (correctly), the
    /// cluster's lineage went with it, and the next run minted another copy; 543 retired copies in a
    /// single repo. A union carries the same claim the pick was reaching for — these observations are
    /// one idea — without asserting that any one of them already stated it. Mirrors
    /// <see cref="StepsContent"/>, which has always composed rather than picked.
    /// </summary>
    private static string DeterministicMerge(IReadOnlyList<MemoryEntry> bucket) =>
        string.Join("\n", bucket
            .OrderBy(o => o.CreatedAt)
            .Select(o => o.Content)
            .Distinct(StringComparer.Ordinal));

    /// <summary>Scan width for the lineage snapshot; matches the observation pool this engine reads.</summary>
    private const int ConsumedScanLimit = 500;

    /// <summary>The ordered union of a cluster's observation contents — the fine Procedure's body.</summary>
    private static string StepsContent(IReadOnlyList<MemoryEntry> bucket) =>
        string.Join("\n", bucket
            .OrderBy(o => o.CreatedAt)
            .Select((o, i) => $"{i + 1}. {o.Content}"));

    /// <summary>The cluster's functional stage: the most common non-<c>None</c> stage among its sources
    /// (ties broken by enum order for determinism), or <c>None</c> when no source carries one. Stage does
    /// NOT partition consolidation groups (#38 decision) — this only classifies an already-formed bucket.</summary>
    private static FunctionalStage DeterminableStage(IReadOnlyList<MemoryEntry> bucket)
    {
        var staged = bucket.Where(o => o.Stage != FunctionalStage.None).ToList();
        return staged.Count == 0
            ? FunctionalStage.None
            : staged.GroupBy(o => o.Stage).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
    }

    /// <summary>
    /// Two-altitude procedure emission (Memp): a fine-grained steps Procedure (the ordered union of the
    /// cluster's observation contents, subtask granularity) and a script-like abstraction Procedure over
    /// it, linked <c>abstraction → fine</c> via <c>DerivedFrom</c> + a <c>"abstracts"</c> MemoryLink. Both
    /// carry the cluster's <paramref name="stage"/> and the anti-laundering provenance. Deterministic;
    /// the abstraction gets optional Ollama polish only on the existing large-bucket path.
    /// </summary>
    private async Task EmitTwoAltitudeProceduresAsync(
        BulkMutationCtx ctx, string repoId, IReadOnlyList<MemoryEntry> bucket, FunctionalStage stage,
        Valence bucketValence, List<string> unionTags, float proposedImportance, MemoryEntry representative,
        List<string> observationIds, ConsolidationResult result, CancellationToken ct)
    {
        var provenance = ProvenanceRules.ForContributors(bucket);
        var now = DateTime.UtcNow;

        var stepsContent = StepsContent(bucket);
        var fine = new MemoryEntry
        {
            Id = MemoryIdGenerator.Generate(repoId, MemoryType.Procedure, stepsContent, now),
            RepoId = repoId,
            Type = MemoryType.Procedure,
            Valence = bucketValence,
            Stage = stage,
            Content = stepsContent,
            Tags = unionTags,
            Importance = proposedImportance,
            Source = "consolidation",
            Provenance = provenance,
            Confidence = 0.7f,
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now },
            DerivedFrom = observationIds,
            Entities = EntityExtractor.Extract(stepsContent),
            OneLiner = EntityExtractor.GenerateHeuristicOneLiner(stepsContent),
        };
        await ctx.StoreNewAsync(fine, ct);

        var abstractContent = representative.Content;
        if (_enrichment.IsAvailable)
        {
            var merged = await _enrichment.MergeObservationsAsync(bucket.Select(o => o.Content).ToList(), ct);
            if (!string.IsNullOrEmpty(merged)) abstractContent = merged;
        }

        // Unlike the Insight path this keeps its representative fallback: an abstraction is a
        // synthesis, and there is no deterministic way to produce one — a union would just restate
        // the fine procedure sitting directly below it. When the fallback does land on a verbatim
        // copy, the fine procedure carries the cluster's lineage independently, so a repair that
        // retires the abstraction no longer un-consolidates the cluster.
        var abstraction = new MemoryEntry
        {
            Id = MemoryIdGenerator.Generate(repoId, MemoryType.Procedure, abstractContent, now.AddTicks(1)),
            RepoId = repoId,
            Type = MemoryType.Procedure,
            Valence = bucketValence,
            Stage = stage,
            Content = abstractContent,
            Tags = unionTags,
            Importance = proposedImportance,
            Source = "consolidation",
            Provenance = provenance,
            Confidence = 0.7f,
            CreatedAt = now.AddTicks(1),
            Validity = new Validity { ValidFrom = now.AddTicks(1) },
            DerivedFrom = new List<string> { fine.Id }.Concat(observationIds).ToList(),
            Links = [new MemoryLink { TargetRepoId = repoId, TargetMemoryId = fine.Id, Relation = "abstracts" }],
            Entities = EntityExtractor.Extract(abstractContent),
            OneLiner = EntityExtractor.GenerateHeuristicOneLiner(abstractContent),
        };
        await ctx.StoreNewAsync(abstraction, ct);

        result.ProceduresCreated += 2;
    }

    public async Task<int> ApplyImportanceDecayAsync(
        string repoId, bool isRepoActive = true, CancellationToken ct = default, BulkMutationCtx? write = null)
    {
        if (!isRepoActive) return 0;

        var now = DateTime.UtcNow;
        var changed = new List<MemoryEntry>();

        foreach (var type in Enum.GetValues<MemoryType>())
        {
            var entries = await _store.GetTopScoredAsync(repoId, [type], 500, ct);

            foreach (var entry in entries)
            {
                var lastTouched = entry.LastAccessedAt ?? entry.CreatedAt;
                if ((now - lastTouched).TotalDays < 7) continue;

                var daysSinceCreation = Math.Max(0, (now - entry.CreatedAt).TotalDays);
                var decayed = FadeMemCurve.Decay(entry.Importance, entry.Confidence, daysSinceCreation, type);

                if (Math.Abs(decayed - entry.Importance) / Math.Max(entry.Importance, 0.01f) < 0.01f)
                    continue;

                entry.Importance = decayed;
                changed.Add(entry);
            }
        }

        if (changed.Count == 0) return 0;

        // Join the caller's bulk scope when handed one (maintenance stage); otherwise own one.
        if (write is { } w)
        {
            foreach (var entry in changed)
                await w.WriteAsync(entry, ct);
        }
        else
        {
            await _memory.UpdateManyAsync(changed, ct);
        }
        return changed.Count;
    }
}

public sealed class ConsolidationResult
{
    public List<ConsolidationCandidate> Candidates { get; set; } = [];
    public int InsightsCreated { get; set; }
    public int InsightsBoosted { get; set; }

    /// <summary>Procedure memories emitted by the two-altitude path (#39) — 2 per staged cluster (fine + abstraction).</summary>
    public int ProceduresCreated { get; set; }
}

public sealed class ConsolidationCandidate
{
    public List<string> ObservationIds { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string ProposedContent { get; set; } = "";
    public float ProposedImportance { get; set; }
}
