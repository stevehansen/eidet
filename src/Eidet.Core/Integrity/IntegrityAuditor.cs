using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Integrity;

/// <summary>
/// Runtime verification of every <see cref="IntegrityCheck"/> over live data.
///
/// Two samples, fetched once per run: the recently INVALIDATED memories (each must be absent from every
/// read path — the same per-memory predicate <c>FamaForgetTests</c> asserts, broadened with the two paths
/// that test does not reach), and the LIVE memories (each must still satisfy its own trust claims). The
/// loop is outer-over-check / inner-over-sample rather than the reverse, because the two halves probe
/// different sets — and as a side benefit <see cref="IntegrityReport.ChecksProbed"/> is complete even on an
/// empty store, which strengthens the coverage guard.
///
/// The <see cref="RunCheckAsync"/> dispatch has one arm per <see cref="IntegrityCheck"/> and throws on an
/// unhandled value: adding a check without a probe fails the coverage test (and any live run) immediately,
/// so the guarantee cannot silently narrow. That <see cref="NotSupportedException"/> is deliberately
/// exempted from the per-check isolation below — swallowing it would defeat the whole guard.
/// </summary>
public sealed class IntegrityAuditor : IIntegrityAuditor
{
    // Memories probed per run, per sample — bounds cost while covering the freshest forgets (the ones most
    // likely to expose a stale index or a missing filter).
    private const int SampleCap = 50;
    private const int ProbeLimit = 50;              // recall / dedup fetch breadth per probe
    private const float ProbeSimilarityFloor = 0.1f; // low floor: maximize the chance a leak is caught
    // Distinct DerivedFrom targets resolved per run. The only reads that scale with lineage breadth rather
    // than with the sample, so they get their own bound; targets beyond it are simply not probed.
    private const int CitationTargetCap = 200;

    private static readonly IntegrityCheck[] AllChecks = Enum.GetValues<IntegrityCheck>();

    private readonly MemoryService _memory;
    private readonly IEidetStore _store;

    public IntegrityAuditor(MemoryService memory, IEidetStore store)
    {
        _memory = memory;
        _store = store;
    }

    public async Task<IntegrityReport> VerifyAsync(string repoId, CancellationToken ct = default)
    {
        var normalized = RepoIdNormalizer.Normalize(repoId);
        var stale = await _store.GetInvalidatedAsync(normalized, SampleCap, ct);
        var live = await _store.BrowseAsync(normalized, 0, SampleCap, ct: ct);
        // Resolved ONCE for the whole run and shared by both citation arms — a target cited by several
        // memories, or examined by both arms, is fetched a single time. LAZY, and awaited inside the
        // dispatch rather than here, so a store failure resolving them lands inside the per-check
        // isolation below: it costs the two citation checks, not the whole suite.
        var citations = new Lazy<Task<IReadOnlyDictionary<string, MemoryEntry?>>>(
            () => ResolveCitationsAsync(live, ct));

        var findings = new List<IntegrityFinding>();
        var probed = new List<IntegrityCheck>();
        foreach (var check in AllChecks)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                findings.AddRange(await RunCheckAsync(check, normalized, stale, live, citations, ct));
                probed.Add(check); // recorded only after a successful dispatch
            }
            catch (NotSupportedException)
            {
                throw; // the coverage guard must fail LOUDLY, never be isolated away
            }
            catch (Exception ex)
            {
                // Isolation: one broken check must not abort the suite. Recorded as a finding rather than
                // swallowed, so a probe that keeps failing shows up instead of quietly narrowing coverage.
                findings.Add(IntegrityFinding.ProbeFailure(check, normalized, ex.Message));
            }
        }

        return new IntegrityReport(normalized, DateTime.UtcNow, stale.Count + live.Count, findings)
        {
            ChecksProbed = probed,
        };
    }

    private Task<List<IntegrityFinding>> RunCheckAsync(
        IntegrityCheck check, string repoId,
        IReadOnlyList<MemoryEntry> stale, IReadOnlyList<MemoryEntry> live,
        Lazy<Task<IReadOnlyDictionary<string, MemoryEntry?>>> citations, CancellationToken ct) => check switch
    {
        IntegrityCheck.Recall =>
            ProbeRecallAsync(repoId, stale, check, crossRepo: false, expandGraph: false, ct),
        IntegrityCheck.CrossRepoSearch =>
            ProbeRecallAsync(repoId, stale, check, crossRepo: true, expandGraph: false, ct),
        IntegrityCheck.GraphNeighbor =>
            ProbeRecallAsync(repoId, stale, check, crossRepo: false, expandGraph: true, ct),
        IntegrityCheck.ContextL1 => ProbeContextL1Async(repoId, stale, ct),
        IntegrityCheck.DuplicateDetection => ProbeDuplicateAsync(repoId, stale, ct),
        IntegrityCheck.UnknownProvenance => Task.FromResult(CheckProvenance(repoId, live)),
        IntegrityCheck.BrokenCommitment => Task.FromResult(CheckCommitments(repoId, live)),
        IntegrityCheck.DanglingCitation or IntegrityCheck.AmendedCitation =>
            CheckCitationsAsync(repoId, live, citations, check),
        _ => throw new NotSupportedException($"No integrity probe for check {check}"),
    };

    // ─── Read-path checks (over the invalidated sample) ──────────────────────

    private async Task<List<IntegrityFinding>> ProbeRecallAsync(
        string repoId, IReadOnlyList<MemoryEntry> stale, IntegrityCheck check,
        bool crossRepo, bool expandGraph, CancellationToken ct)
    {
        var found = new List<IntegrityFinding>();
        foreach (var memory in stale)
        {
            if (ct.IsCancellationRequested) break;
            var results = await _memory.RecallAsync(repoId, new RecallOptions(memory.Content)
            {
                CrossRepo = crossRepo,
                ExpandGraph = expandGraph,
                Limit = ProbeLimit,
            }, ct);
            if (results.Any(r => r.Id == memory.Id))
                found.Add(new IntegrityFinding(memory.Id, check, repoId,
                    $"resurfaced in recall (crossRepo={crossRepo}, expandGraph={expandGraph})"));
        }
        return found;
    }

    private async Task<List<IntegrityFinding>> ProbeContextL1Async(
        string repoId, IReadOnlyList<MemoryEntry> stale, CancellationToken ct)
    {
        if (stale.Count == 0) return [];

        // Probe the exact mechanism L1 wake-up context uses (GetTopScoredAsync over the L1 types) so the
        // check is id-precise rather than string-matching the rendered block. One fetch covers the whole
        // sample — the candidate set does not depend on which stale memory we are asking about.
        var l1 = await _store.GetTopScoredAsync(
            repoId, [MemoryType.Insight, MemoryType.Procedure, MemoryType.Heuristic], 60, ct);
        var present = new HashSet<string>(l1.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);

        return stale
            .Where(m => present.Contains(m.Id))
            .Select(m => new IntegrityFinding(
                m.Id, IntegrityCheck.ContextL1, repoId, "present in L1 top-scored candidates"))
            .ToList();
    }

    private async Task<List<IntegrityFinding>> ProbeDuplicateAsync(
        string repoId, IReadOnlyList<MemoryEntry> stale, CancellationToken ct)
    {
        var found = new List<IntegrityFinding>();
        foreach (var memory in stale)
        {
            if (ct.IsCancellationRequested) break;

            var dup = await _store.FindDuplicateAsync(repoId, memory.Content, ProbeSimilarityFloor, ct);
            if (dup?.Id == memory.Id)
            {
                found.Add(new IntegrityFinding(memory.Id, IntegrityCheck.DuplicateDetection, repoId,
                    "returned by exact-duplicate detection"));
                continue;
            }

            // FindNearDuplicatesAsync excludes the probe entry by id, so probe with a same-content clone
            // under a distinct id — a stale memory must not surface as a near-duplicate of a new write.
            var probe = new MemoryEntry
            {
                Id = memory.Id + "#integrity-probe", RepoId = memory.RepoId, Type = memory.Type, Content = memory.Content,
            };
            var near = await _store.FindNearDuplicatesAsync(repoId, probe, ProbeSimilarityFloor, ProbeLimit, ct);
            if (near.Any(e => e.Id == memory.Id))
                found.Add(new IntegrityFinding(memory.Id, IntegrityCheck.DuplicateDetection, repoId,
                    "returned by near-duplicate detection"));
        }
        return found;
    }

    // ─── Trust-claim checks (over the live sample) ───────────────────────────

    private static List<IntegrityFinding> CheckProvenance(string repoId, IReadOnlyList<MemoryEntry> live) =>
        live
            .Where(e => e.Provenance == MemoryProvenance.Unknown)
            .Select(e => new IntegrityFinding(e.Id, IntegrityCheck.UnknownProvenance, repoId,
                string.IsNullOrWhiteSpace(e.Source)
                    ? "provenance never established and no source to derive it from"
                    : $"provenance never established; source=\"{e.Source}\""))
            .ToList();

    private static List<IntegrityFinding> CheckCommitments(string repoId, IReadOnlyList<MemoryEntry> live) =>
        live
            .Where(e => MemoryCommitment.Check(e) is CommitmentStatus.Broken)
            .Select(e => new IntegrityFinding(e.Id, IntegrityCheck.BrokenCommitment, repoId,
                "content does not re-derive its own id — rewritten in place instead of superseded"))
            .ToList();

    /// <summary>
    /// Both citation arms await the one memoized resolution, so a shared target costs a single store read
    /// and a resolution failure is attributed to these two checks alone. Awaiting a faulted
    /// <see cref="Lazy{T}"/> rethrows for each arm, so both are reported as unprobed rather than one
    /// silently passing.
    /// </summary>
    private static async Task<List<IntegrityFinding>> CheckCitationsAsync(
        string repoId, IReadOnlyList<MemoryEntry> live,
        Lazy<Task<IReadOnlyDictionary<string, MemoryEntry?>>> citations, IntegrityCheck check) =>
        CheckCitations(repoId, live, await citations.Value, check);

    /// <summary>
    /// A target beyond <see cref="CitationTargetCap"/> is absent from the map and reported by neither arm —
    /// unprobed, not clean.
    /// </summary>
    private static List<IntegrityFinding> CheckCitations(
        string repoId, IReadOnlyList<MemoryEntry> live,
        IReadOnlyDictionary<string, MemoryEntry?> citations, IntegrityCheck check)
    {
        var found = new List<IntegrityFinding>();
        foreach (var entry in live)
        {
            foreach (var citedId in entry.DerivedFrom)
            {
                if (!citations.TryGetValue(citedId, out var target)) continue;

                if (check == IntegrityCheck.DanglingCitation && target is null)
                    found.Add(new IntegrityFinding(entry.Id, check, repoId,
                        $"cites [{citedId}], which no longer exists"));
                else if (check == IntegrityCheck.AmendedCitation
                         && target is not null && MemoryCommitment.Check(target) is CommitmentStatus.Amended)
                    found.Add(new IntegrityFinding(entry.Id, check, repoId,
                        $"cites [{citedId}], whose content was amended after the citation was made"));
            }
        }
        return found;
    }

    private async Task<IReadOnlyDictionary<string, MemoryEntry?>> ResolveCitationsAsync(
        IReadOnlyList<MemoryEntry> live, CancellationToken ct)
    {
        var targets = live
            .SelectMany(e => e.DerivedFrom)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(CitationTargetCap)
            .ToList();

        // One batched round trip for the whole cited set — the cap bounds the payload, not the number of
        // requests, and this runs on the nightly maintenance pass and the quality dashboard alike.
        return await _store.GetManyAsync(targets, ct);
    }
}
