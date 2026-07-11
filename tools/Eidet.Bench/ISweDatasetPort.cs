namespace Eidet.Bench;

/// <summary>
/// External touch #1: the SWE Context Bench task source. <see cref="FixtureDataset"/> is the
/// bundled offline adapter; the real parquet adapter is Phase 1 of issue #36 and sits behind
/// this same port.
/// </summary>
public interface ISweDatasetPort
{
    /// <summary>Human-readable dataset identity, rendered into the report header.</summary>
    string Name { get; }

    /// <summary>
    /// True only for the genuine SWE Context Bench release. <see cref="LeaderboardGuard"/> and
    /// <see cref="SweBenchReport.ToMarkdown"/> key the anti-misreporting refusal/banner off this —
    /// a fixture run can never be presented as a leaderboard number.
    /// </summary>
    bool IsRealDataset { get; }

    bool IsAvailable { get; }

    /// <summary>
    /// Loads tasks in ingestion order (related tasks before the base tasks that benefit from
    /// them). <paramref name="limit"/> caps the number of base (evaluated) tasks; related tasks
    /// ride along uncapped. Non-positive means all.
    /// </summary>
    Task<IReadOnlyList<SweTask>> LoadAsync(int limit, CancellationToken ct = default);
}
