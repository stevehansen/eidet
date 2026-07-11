using System.Runtime.CompilerServices;

namespace Eidet.Core.Intake.Git;

/// <summary>
/// Test-only adapter (the <c>InMemoryEnrichmentAdapter</c> analogue). Callers append commits
/// oldest-to-newest with <see cref="AddCommit"/>; enumeration returns them newest first like
/// <c>git log</c>. Lets the whole gate→mine→secret-skip→dedup pipeline run against fixtures
/// with no repo and no subprocess.
/// </summary>
public sealed class InMemoryGitHistorySource : IGitHistorySource
{
    private readonly List<CommitRecord> _commits = [];
    private readonly Dictionary<string, List<DiffHunk>> _diffs = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// Append a commit as the new tip. <paramref name="files"/> become +1/−1 modifications;
    /// <paramref name="hunk"/> (newline-separated ± lines) becomes a single context-less diff
    /// hunk on the first file — use <see cref="AddDiff"/> for full hunk control.
    /// </summary>
    public InMemoryGitHistorySource AddCommit(
        string sha, string subject, string? body = null,
        IReadOnlyList<string>? files = null, string? hunk = null, bool isMerge = false,
        string authorEmail = "dev@local", DateTimeOffset? committedAt = null)
    {
        var changes = (files ?? []).Select(f => new FileChange(f, 1, 1, ChangeKind.Modified)).ToList();
        _commits.Add(new CommitRecord(
            sha, subject, body ?? "", authorEmail,
            committedAt ?? DateTimeOffset.UtcNow, isMerge, changes));
        if (hunk is not null && changes.Count > 0)
            _diffs[sha] = [new DiffHunk(changes[0].Path, "@@ -1,1 +1,1 @@", hunk.Split('\n'))];
        return this;
    }

    /// <summary>Attach an explicit diff hunk to an already-added commit.</summary>
    public InMemoryGitHistorySource AddDiff(string sha, string path, string header, params string[] lines)
    {
        if (!_diffs.TryGetValue(sha, out var hunks))
            _diffs[sha] = hunks = [];
        hunks.Add(new DiffHunk(path, header, lines));
        return this;
    }

    public async IAsyncEnumerable<CommitRecord> ReadMergedHistoryAsync(
        GitHistoryQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var yielded = 0;
        for (var i = _commits.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var commit = _commits[i];
            if (commit.Sha == query.Since) yield break;
            if (query.MergesOnly && !commit.IsMerge) continue;
            yield return commit;
            if (++yielded >= query.MaxCommits) yield break;
        }
    }

    public async IAsyncEnumerable<DiffHunk> ReadDiffAsync(
        string sha, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        if (!_diffs.TryGetValue(sha, out var hunks)) yield break;
        foreach (var hunk in hunks)
        {
            ct.ThrowIfCancellationRequested();
            yield return hunk;
        }
    }
}
