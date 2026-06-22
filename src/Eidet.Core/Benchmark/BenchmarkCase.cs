using Eidet.Core.Storage;

namespace Eidet.Core.Benchmark;

/// <summary>
/// One gold case: a query's scripted candidate pool with per-arm raw scores, the relevant
/// (gold) ids, the AMA-Bench capability it exercises, and the retrieval budget <see cref="K"/>.
/// The runner feeds <see cref="Lex"/>/<see cref="Vec"/> through the real ranking math and scores
/// the result against <see cref="GoldIds"/>. A case is self-contained — no external data, no clock
/// beyond the <c>now</c> the runner supplies — so every metric is reproducible.
/// </summary>
public sealed record BenchmarkCase(
    string Id,
    AmaCapability Capability,
    IReadOnlyList<ScoredHit> Lex,
    IReadOnlyList<ScoredHit> Vec,
    IReadOnlySet<string> GoldIds,
    int K);
