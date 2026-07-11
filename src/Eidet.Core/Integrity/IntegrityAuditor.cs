using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Integrity;

/// <summary>
/// Runtime post-forget verification over live data. For each recently soft-deleted memory it drives
/// every <see cref="ReadPath"/> and records a leak if the memory resurfaces — the same per-memory
/// predicate <c>FamaForgetTests</c> asserts (absent from recall / context), lifted to run over
/// sampled production memories and broadened with the two paths that test does not reach
/// (<see cref="ReadPath.GraphNeighbor"/>, <see cref="ReadPath.DuplicateDetection"/>).
///
/// The <see cref="ProbeAsync"/> dispatch has one arm per <see cref="ReadPath"/> and throws on an
/// unhandled value: adding a read path without a probe fails the coverage test (and any live run)
/// immediately, so the guarantee cannot silently narrow.
/// </summary>
public sealed class IntegrityAuditor : IIntegrityAuditor
{
    // Recently-invalidated memories probed per run — bounds cost while covering the freshest forgets
    // (the ones most likely to expose a stale index or a missing filter).
    private const int SampleCap = 50;
    private const int ProbeLimit = 50;              // recall / dedup fetch breadth per probe
    private const float ProbeSimilarityFloor = 0.1f; // low floor: maximize the chance a leak is caught

    private static readonly ReadPath[] AllPaths = Enum.GetValues<ReadPath>();

    private readonly MemoryService _memory;
    private readonly IEidetStore _store;

    public IntegrityAuditor(MemoryService memory, IEidetStore store)
    {
        _memory = memory;
        _store = store;
    }

    public async Task<IntegrityReport> VerifyForgottenAsync(string repoId, CancellationToken ct = default)
    {
        var normalized = RepoIdNormalizer.Normalize(repoId);
        var stale = await _store.GetInvalidatedAsync(normalized, SampleCap, ct);

        var leaks = new List<IntegrityLeak>();
        var probed = new HashSet<ReadPath>();
        foreach (var memory in stale)
        {
            if (ct.IsCancellationRequested) break;
            foreach (var path in AllPaths)
            {
                var leak = await ProbeAsync(path, normalized, memory, ct);
                probed.Add(path); // recorded only after a successful dispatch — an unhandled path throws first
                if (leak is { } l) leaks.Add(l);
            }
        }

        return new IntegrityReport(normalized, DateTime.UtcNow, stale.Count, leaks)
        {
            PathsProbed = probed.ToList(),
        };
    }

    private Task<IntegrityLeak?> ProbeAsync(ReadPath path, string repoId, MemoryEntry stale, CancellationToken ct) => path switch
    {
        ReadPath.Recall => ProbeRecallAsync(repoId, stale, ReadPath.Recall, crossRepo: false, expandGraph: false, ct),
        ReadPath.CrossRepoSearch => ProbeRecallAsync(repoId, stale, ReadPath.CrossRepoSearch, crossRepo: true, expandGraph: false, ct),
        ReadPath.GraphNeighbor => ProbeRecallAsync(repoId, stale, ReadPath.GraphNeighbor, crossRepo: false, expandGraph: true, ct),
        ReadPath.ContextL1 => ProbeContextL1Async(repoId, stale, ct),
        ReadPath.DuplicateDetection => ProbeDuplicateAsync(repoId, stale, ct),
        _ => throw new NotSupportedException($"No integrity probe for read path {path}"),
    };

    private async Task<IntegrityLeak?> ProbeRecallAsync(
        string repoId, MemoryEntry stale, ReadPath path, bool crossRepo, bool expandGraph, CancellationToken ct)
    {
        var results = await _memory.RecallAsync(repoId, new RecallOptions(stale.Content)
        {
            CrossRepo = crossRepo,
            ExpandGraph = expandGraph,
            Limit = ProbeLimit,
        }, ct);
        return results.Any(r => r.Id == stale.Id)
            ? new IntegrityLeak(stale.Id, path, repoId, $"resurfaced in recall (crossRepo={crossRepo}, expandGraph={expandGraph})")
            : null;
    }

    private async Task<IntegrityLeak?> ProbeContextL1Async(string repoId, MemoryEntry stale, CancellationToken ct)
    {
        // Probe the exact mechanism L1 wake-up context uses (GetTopScoredAsync over the L1 types) so
        // the check is id-precise rather than string-matching the rendered block.
        var l1 = await _store.GetTopScoredAsync(
            repoId, [MemoryType.Insight, MemoryType.Procedure, MemoryType.Heuristic], 60, ct);
        return l1.Any(e => e.Id == stale.Id)
            ? new IntegrityLeak(stale.Id, ReadPath.ContextL1, repoId, "present in L1 top-scored candidates")
            : null;
    }

    private async Task<IntegrityLeak?> ProbeDuplicateAsync(string repoId, MemoryEntry stale, CancellationToken ct)
    {
        var dup = await _store.FindDuplicateAsync(repoId, stale.Content, ProbeSimilarityFloor, ct);
        if (dup?.Id == stale.Id)
            return new IntegrityLeak(stale.Id, ReadPath.DuplicateDetection, repoId, "returned by exact-duplicate detection");

        // FindNearDuplicatesAsync excludes the probe entry by id, so probe with a same-content clone
        // under a distinct id — a stale memory must not surface as a near-duplicate of a new write.
        var probe = new MemoryEntry { Id = stale.Id + "#integrity-probe", RepoId = stale.RepoId, Type = stale.Type, Content = stale.Content };
        var near = await _store.FindNearDuplicatesAsync(repoId, probe, ProbeSimilarityFloor, ProbeLimit, ct);
        return near.Any(e => e.Id == stale.Id)
            ? new IntegrityLeak(stale.Id, ReadPath.DuplicateDetection, repoId, "returned by near-duplicate detection")
            : null;
    }
}
