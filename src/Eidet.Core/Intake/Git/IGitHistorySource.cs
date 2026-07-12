namespace Eidet.Core.Intake.Git;

/// <summary>How a commit touched a file, as far as the history source can tell.</summary>
public enum ChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
}

/// <summary>
/// One commit yielded by an <see cref="IGitHistorySource"/> — pure data, no git-library or
/// process types leak upward.
/// </summary>
public sealed record CommitRecord(
    string Sha,
    string Subject,
    string Body,
    string AuthorEmail,
    DateTimeOffset CommittedAt,
    bool IsMerge,
    IReadOnlyList<FileChange> Files);

/// <summary>Per-file change stats for one commit (numstat-style added/removed line counts).</summary>
public sealed record FileChange(string Path, int Added, int Removed, ChangeKind Kind);

/// <summary>
/// One diff hunk. <see cref="Lines"/> carries the raw ± lines — secret-bearing content —
/// so consumers must mine patterns from it (headers, stats), never store it verbatim.
/// </summary>
public sealed record DiffHunk(string Path, string Header, IReadOnlyList<string> Lines);

/// <summary>
/// Port-level history slice. <see cref="Since"/> is an exclusive lower bound (a commit SHA);
/// <see cref="MergesOnly"/> restricts to merge commits for PR-merge-style repos — off by
/// default because squash-merge repos would otherwise yield nothing before any gating runs.
/// </summary>
public sealed record GitHistoryQuery(int MaxCommits = 500, string? Since = null, bool MergesOnly = false);

/// <summary>
/// Read-only git history port. Adapters mirror the enrichment port/adapter shape:
/// <c>GitCliAdapter</c> (subprocess, the only type that touches raw git output),
/// <c>NullGitHistorySource</c> (unavailable), and <see cref="InMemoryGitHistorySource"/>
/// (test fixtures, zero subprocess). Diff reads are lazy and per-commit so raw hunks —
/// the secret-bearing content — are never bulk-materialized.
/// </summary>
public interface IGitHistorySource
{
    /// <summary>False when there is no repo / no usable git — consumers no-op.</summary>
    bool IsAvailable { get; }

    /// <summary>Commits reachable from the tip, newest first, bounded by the query.</summary>
    IAsyncEnumerable<CommitRecord> ReadMergedHistoryAsync(GitHistoryQuery query, CancellationToken ct = default);

    /// <summary>Hunks of one commit's diff. Empty for merge commits and unknown SHAs.</summary>
    IAsyncEnumerable<DiffHunk> ReadDiffAsync(string sha, CancellationToken ct = default);
}
