namespace Eidet.Core.Intake.Git;

/// <summary>Unavailable-git singleton — <see cref="GitHistoryExtractor"/> no-ops on it.</summary>
internal sealed class NullGitHistorySource : IGitHistorySource
{
    public static readonly NullGitHistorySource Instance = new();

    private NullGitHistorySource()
    {
    }

    public bool IsAvailable => false;

    public IAsyncEnumerable<CommitRecord> ReadMergedHistoryAsync(GitHistoryQuery query, CancellationToken ct = default) =>
        Empty<CommitRecord>();

    public IAsyncEnumerable<DiffHunk> ReadDiffAsync(string sha, CancellationToken ct = default) =>
        Empty<DiffHunk>();

    private static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
