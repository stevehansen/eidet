using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;

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
        // Partition each tag group by valence sign so opposite stances consolidate independently
        // and a contradiction is never collapsed into one insight.
        foreach (var group in groups)
        foreach (var bucket in group.GroupBy(o => ValencePolarity.Sign(o.Valence)).Select(g => g.ToList()))
        {
            if (bucket.Count < 3) continue;

            var bucketValence = bucket.FirstOrDefault(o => o.Valence != Valence.Neutral)?.Valence ?? Valence.Neutral;
            var unionTags = bucket.SelectMany(o => o.Tags).Distinct().ToList();
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
            var existingInsight = await _store.FindDuplicateAsync(repoId, representative.Content, 0.85f, ct);
            if (existingInsight is not null && existingInsight.Type == MemoryType.Insight &&
                !ValencePolarity.Conflicts(existingInsight.Valence, bucketValence))
            {
                // Anti-laundering (boost path): only trusted sources may lift a trusted insight. An
                // attacker must not be able to raise a good insight's importance — or contaminate its
                // lineage — by injecting low-trust (Pack/Intake) observations that happen to match it.
                // No trusted contributors → skip the boost entirely (a "compression-amplified toxin").
                var trusted = bucket.Where(IsTrustedSource).ToList();
                if (trusted.Count == 0) continue;

                existingInsight.Importance = Math.Min(1.0f, existingInsight.Importance + 0.05f * trusted.Count);
                existingInsight.DerivedFrom = existingInsight.DerivedFrom
                    .Concat(trusted.Select(o => o.Id))
                    .Distinct()
                    .ToList();
                await ctx.WriteAsync(existingInsight, ct);
                result.InsightsBoosted++;
            }
            else
            {
                var mergedContent = representative.Content;
                if (bucket.Count > 5 && _enrichment.IsAvailable)
                {
                    var merged = await _enrichment.MergeObservationsAsync(
                        bucket.Select(o => o.Content).ToList(), ct);
                    if (!string.IsNullOrEmpty(merged))
                        mergedContent = merged;
                }

                // Anti-laundering (create path): if ANY contributing observation is untrusted
                // (Pack/Intake), stamp the new insight with the least-trusted contributor's
                // provenance instead of Consolidation, so MemoryTrust.Factor keeps demoting it at
                // recall. This stops an attacker laundering a poisoned observation into a fully
                // trusted insight ("compression-amplified toxin"). The audit trail — Source and
                // DerivedFrom — is preserved untouched.
                var provenance = ProvenanceFor(bucket);

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

    /// <summary>An observation counts as a trusted source unless its origin is a known poisoning
    /// surface (Pack/Intake), i.e. its provenance trust floor is the full 1.0.</summary>
    private static bool IsTrustedSource(MemoryEntry obs) =>
        MemoryTrust.ProvenanceTrust(obs.Provenance) >= 1.0;

    /// <summary>Anti-laundering provenance stamp: any untrusted contributor demotes the emission to the
    /// least-trusted contributor's provenance; an all-trusted bucket earns <c>Consolidation</c>.</summary>
    private static MemoryProvenance ProvenanceFor(IReadOnlyList<MemoryEntry> bucket) =>
        bucket.Any(o => !IsTrustedSource(o))
            ? bucket.OrderBy(o => MemoryTrust.ProvenanceTrust(o.Provenance)).First().Provenance
            : MemoryProvenance.Consolidation;

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
        var provenance = ProvenanceFor(bucket);
        var now = DateTime.UtcNow;

        var stepsContent = string.Join("\n", bucket
            .OrderBy(o => o.CreatedAt)
            .Select((o, i) => $"{i + 1}. {o.Content}"));
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
        if (bucket.Count > 5 && _enrichment.IsAvailable)
        {
            var merged = await _enrichment.MergeObservationsAsync(bucket.Select(o => o.Content).ToList(), ct);
            if (!string.IsNullOrEmpty(merged)) abstractContent = merged;
        }
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
