using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Benchmark;

/// <summary>
/// One gold case: a query's scripted candidate pool with per-arm raw scores, the relevant
/// (gold) ids, the AMA-Bench capability it exercises, and the retrieval budget <see cref="K"/>.
/// The runner feeds <see cref="Lex"/>/<see cref="Vec"/> through the real ranking math and scores
/// the result against <see cref="GoldIds"/>. A case is self-contained — no external data, no clock
/// beyond the <c>now</c> the runner supplies — so every metric is reproducible.
/// <para><see cref="Neighbors"/> are off-pool, link-reachable entries (in neither arm) the runner can
/// resolve when a parent hit carries a <see cref="MemoryLink.TargetMemoryId"/> into them — exercising
/// graph-neighbor expansion. Empty (the default) makes expansion a no-op, so a case without neighbors
/// scores byte-identically with or without the expansion pass.</para>
/// </summary>
public sealed record BenchmarkCase(
    string Id,
    AmaCapability Capability,
    IReadOnlyList<ScoredHit> Lex,
    IReadOnlyList<ScoredHit> Vec,
    IReadOnlySet<string> GoldIds,
    int K)
{
    public IReadOnlyDictionary<string, MemoryEntry> Neighbors { get; init; } =
        new Dictionary<string, MemoryEntry>();
}
