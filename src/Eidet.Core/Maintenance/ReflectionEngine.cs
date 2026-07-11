using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Gates;
using Eidet.Core.LooseEnds;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Maintenance;

/// <summary>Which residue arms a reflection run mines: everything, or one source in isolation.</summary>
public enum ReflectionSource { All, Echoes, LooseEnds, Drift }

/// <summary>
/// The ACE-style Reflector — the synthesis counterpart to <see cref="ConsolidationEngine"/>. Where
/// DriftReview and RoiDecay <i>move scores</i> on existing memories, the Reflector mints NET-NEW
/// itemized memories from POSITIVE feedback residue (net-echoed memories, Done loose ends, Contradicted
/// drift verdicts) through one maintenance-time LLM call, then routes each proposal through Eidet's
/// existing write gates. Exposed publicly so API / MCP / scheduler can run it in dry-run or stand-alone
/// mode. Ships dormant — the pipeline stage no-ops unless <see cref="ReflectionConfig.Enabled"/> is set.
///
/// Anti-laundering is enforced at three layers here: the model returns advisory content only (all
/// trust-bearing fields are engine-owned), synthesized text is secret+signal gated by
/// <see cref="WriteValidator"/> (consolidation can skip this because its text is pre-gated; reflection
/// CANNOT — the content is LLM-fresh), and a new memory derived from any below-full-trust contributor
/// inherits the least-trusted contributor's provenance instead of <see cref="MemoryProvenance.Reflection"/>.
/// </summary>
public sealed class ReflectionEngine
{
    private readonly IEidetStore _store;
    private readonly EnrichmentService _enrichment;
    private readonly MemoryService _memory;
    private readonly ILooseEndStore? _looseEnds;
    private readonly ReflectionConfig _config;

    // Aggressive near-duplicate suppression (matches ConsolidationEngine's existing-insight probe) — the
    // primary bound on LLM over-generation alongside the NightlyBatch cap and dormant-by-default gate.
    private const float DuplicateThreshold = 0.85f;

    // Engine-owned defaults for a fresh reflected memory. NOT model-owned: Importance and Confidence are
    // stamped here so a proposal can never launder itself into a high-importance, high-confidence lineage.
    private const float DefaultImportance = 0.5f;
    private const float DefaultConfidence = 0.7f;

    public ReflectionEngine(
        IEidetStore store, EnrichmentService? enrichment, MemoryService memory,
        ILooseEndStore? looseEnds = null, ReflectionConfig? config = null)
    {
        _store = store;
        _enrichment = enrichment ?? EnrichmentService.CreateNull();
        _memory = memory;
        _looseEnds = looseEnds;
        _config = config ?? new ReflectionConfig();
    }

    /// <summary>The reflection tuning this engine runs with — the single source of truth for
    /// <see cref="ReflectionConfig.Enabled"/> and the batch/threshold knobs, read by the pipeline
    /// stage so the enable gate and the mining knobs can never diverge across two config instances.</summary>
    public ReflectionConfig Config => _config;

    public async Task<ReflectionResult> ReflectAsync(
        string repoId, bool dryRun = false, ReflectionSource source = ReflectionSource.All,
        CancellationToken ct = default, BulkMutationCtx? write = null)
    {
        var result = new ReflectionResult();

        // Offline short-circuit BEFORE the cursor is read or advanced. With no reachable enrichment
        // backend there is nothing to synthesize, and advancing the cursor here would silently skip
        // residue that was never actually reflected — the empty proposal set would mean "we never
        // asked the model", not "nothing new". This is also the sole availability gate; the pipeline
        // stage's own IsAvailable check is now redundant but harmless, and the REST/CLI handler (which
        // calls this method directly, bypassing the stage) inherits the guard for free.
        if (!_enrichment.IsAvailable) return result;

        var now = DateTime.UtcNow;

        var cursor = await _store.GetLastReflectedAtAsync(repoId, ct);
        var residue = await AssembleResidueAsync(repoId, source, cursor, now, ct);

        var proposals = residue.IsEmpty
            ? []
            : await _enrichment.ProposeReflectionsAsync(residue, ct);

        if (proposals.Count > 0)
        {
            // Contributors for trust/lineage = the MEMORY residue only. Loose ends are first-party
            // parked work with no provenance to launder, so they inform content but not the trust stamp.
            var contributors = residue.EchoedMemories.Concat(residue.Contradicted).ToList();
            var provenance = contributors.Any(m => !IsTrustedSource(m))
                ? contributors.OrderBy(m => MemoryTrust.ProvenanceTrust(m.Provenance)).First().Provenance
                : MemoryProvenance.Reflection;
            var derivedFrom = contributors.Select(m => m.Id).Distinct().ToList();

            if (dryRun)
                await MintAsync(null, repoId, proposals, provenance, derivedFrom, now, dryRun: true, result, ct);
            else if (write is { } w)
                await MintAsync(w, repoId, proposals, provenance, derivedFrom, now, dryRun: false, result, ct);
            else
                await _memory.RunBulkAsync(
                    ctx => MintAsync(ctx, repoId, proposals, provenance, derivedFrom, now, dryRun: false, result, ct),
                    new BulkOptions { OperationName = "reflect" }, ct);
        }

        // Advance the coverage cursor after any live run (even one that minted nothing) so the window
        // walks forward and the same residue is not re-fed on the next pass.
        //
        // v1 semantics (deliberate): selection within a window is bounded by NightlyBatch and
        // prioritized (echoes → contradictions → loose ends). Qualifying overflow beyond the cap is
        // dropped, not carried — a single temporal cursor cannot represent a partially-consumed window.
        // Echoed/contradicted residue re-qualifies as it keeps accruing signal, but a trimmed resolved
        // loose end is one-shot and will be missed. Acceptable while the feature is dormant + additive;
        // a per-arm watermark is the follow-up if run volume ever exceeds the batch cap in practice.
        if (!dryRun)
            await _store.SetLastReflectedAtAsync(repoId, now, ct);

        return result;
    }

    private async Task<ReflectionResidue> AssembleResidueAsync(
        string repoId, ReflectionSource source, DateTime? cursor, DateTime now, CancellationToken ct)
    {
        var batch = Math.Max(0, _config.NightlyBatch);
        List<MemoryEntry> echoed = [];
        List<MemoryEntry> contradicted = [];
        IReadOnlyList<LooseEnd> ends = [];

        if (batch > 0 && (source is ReflectionSource.All or ReflectionSource.Echoes or ReflectionSource.Drift))
        {
            var corpus = (await _store.GetTopScoredAsync(repoId, Enum.GetValues<MemoryType>(), 500, ct))
                .Where(m => m.IsLatest && m.LayerId == null && m.Validity.IsValidAt(now))
                .ToList();

            if (source is ReflectionSource.All or ReflectionSource.Echoes)
                echoed = corpus
                    .Where(m => m.EchoCount - m.FizzleCount >= _config.MinEchoes
                        && Newer(m.LastAccessedAt ?? m.CreatedAt, cursor))
                    .Take(batch)
                    .ToList();

            if (source is ReflectionSource.All or ReflectionSource.Drift)
                contradicted = corpus
                    .Where(m => m.Drift is { Verdict: DriftVerdictKind.Contradicted } d && Newer(d.ReviewedAt, cursor))
                    .Take(batch)
                    .ToList();
        }

        if (batch > 0 && _looseEnds is not null && source is ReflectionSource.All or ReflectionSource.LooseEnds)
        {
            var since = cursor is { } c ? new DateTimeOffset(DateTime.SpecifyKind(c, DateTimeKind.Utc)) : (DateTimeOffset?)null;
            ends = await _looseEnds.ListResolvedUnpromotedAsync(repoId, since, batch, ct);
        }

        // Enforce a single per-RUN cap (NightlyBatch is the primary over-generation bound). Each arm is
        // already capped to bound the DB read; this cascade caps the TOTAL fed to the model so `source:All`
        // can't send up to 3×batch. Priority: echoes (positive reinforcement) → contradictions → loose ends.
        var budget = batch;
        echoed = echoed.Take(budget).ToList();
        budget -= echoed.Count;
        contradicted = contradicted.Take(Math.Max(0, budget)).ToList();
        budget -= contradicted.Count;
        ends = ends.Take(Math.Max(0, budget)).ToList();

        return new ReflectionResidue(repoId, echoed, ends, contradicted);
    }

    private async Task<int> MintAsync(
        BulkMutationCtx? ctx, string repoId, IReadOnlyList<ReflectionProposal> proposals,
        MemoryProvenance provenance, List<string> derivedFrom, DateTime now, bool dryRun,
        ReflectionResult result, CancellationToken ct)
    {
        foreach (var p in proposals)
        {
            if (ct.IsCancellationRequested) break;

            // (a) MANDATORY secret+signal gate. Consolidation skips this because its text is pre-gated
            // observation content; reflection text is LLM-fresh and MUST be validated before storage.
            if (!WriteValidator.Validate(p.Content, p.Type).Passed) continue;

            // (b) Duplicate guard — skip a near-duplicate unless it takes the OPPOSITE hard stance
            // (a real contradiction we want to keep alongside the existing claim).
            var dup = await _store.FindDuplicateAsync(repoId, p.Content, DuplicateThreshold, ct);
            if (dup is not null && !ValencePolarity.Conflicts(dup.Valence, p.Valence)) continue;

            var candidate = new ReflectionCandidate
            {
                Content = p.Content,
                Type = p.Type,
                Valence = p.Valence,
                Tags = p.Tags.ToList(),
                DerivedFrom = derivedFrom,
                Importance = DefaultImportance,
                Provenance = provenance,
            };
            result.Candidates.Add(candidate);

            if (dryRun || ctx is not { } w) continue;

            var entry = new MemoryEntry
            {
                Id = MemoryIdGenerator.Generate(repoId, p.Type, p.Content, now),
                RepoId = repoId,
                Type = p.Type,
                Valence = p.Valence,
                Content = p.Content,
                Tags = candidate.Tags,
                Importance = DefaultImportance,
                Source = "reflection",
                Provenance = provenance,
                Confidence = DefaultConfidence,
                CreatedAt = now,
                Validity = new Validity { ValidFrom = now },
                DerivedFrom = derivedFrom,
                Entities = EntityExtractor.Extract(p.Content),
                OneLiner = EntityExtractor.GenerateHeuristicOneLiner(p.Content),
            };
            await w.StoreNewAsync(entry, ct);
            result.MemoriesCreated++;
        }
        return result.MemoriesCreated;
    }

    /// <summary>A contributor counts as trusted only when its provenance trust floor is the full 1.0
    /// (i.e. not Pack/Intake/Reflection). Mirrors ConsolidationEngine.IsTrustedSource.</summary>
    private static bool IsTrustedSource(MemoryEntry m) =>
        MemoryTrust.ProvenanceTrust(m.Provenance) >= 1.0;

    private static bool Newer(DateTime candidate, DateTime? cursor) =>
        cursor is null || candidate > cursor.Value;
}

public sealed class ReflectionResult
{
    public List<ReflectionCandidate> Candidates { get; set; } = [];
    public int MemoriesCreated { get; set; }
}

public sealed class ReflectionCandidate
{
    public string Content { get; set; } = "";
    public MemoryType Type { get; set; }
    public Valence Valence { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<string> DerivedFrom { get; set; } = [];
    public float Importance { get; set; }
    public MemoryProvenance Provenance { get; set; }
}
