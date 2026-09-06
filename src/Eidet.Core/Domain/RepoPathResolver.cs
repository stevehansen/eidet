namespace Eidet.Core.Domain;

/// <summary>
/// Maps the directory an agent is working in to the repository its memories belong to, before
/// <see cref="RepoIdNormalizer"/> turns that into a namespace.
///
/// A git worktree is a second checkout of one repository, and its path is routinely temporary — a
/// PR branch under <c>.claude-worktrees/</c>, or a session scratchpad under the system temp
/// directory. Left alone each checkout becomes its own repo namespace, so memories written from it
/// are unreachable from the repository they actually describe and are stranded when the directory
/// goes away. Resolving to the main repository is what puts a worktree session and a primary-checkout
/// session in the same namespace.
///
/// Total by construction: a plain directory, an already-normalized repo id, a path that no longer
/// exists, a submodule, or a malformed pointer all come back unchanged. Callers hand in a path and
/// get a path — never an exception, because a repo identity that can fail is a store that can fail.
/// </summary>
public static class RepoPathResolver
{
    private const string GitDirPrefix = "gitdir:";

    /// <summary>The segment git uses to nest a worktree's private directory inside the main repo.</summary>
    private const string WorktreesSegment = "worktrees";

    public static string Resolve(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return directoryPath;

        try
        {
            // A worktree marks itself with a .git *file*; a primary checkout has a .git directory.
            var marker = Path.Combine(directoryPath, ".git");
            if (!File.Exists(marker)) return directoryPath;

            var pointer = File.ReadAllText(marker).Trim();
            if (!pointer.StartsWith(GitDirPrefix, StringComparison.OrdinalIgnoreCase)) return directoryPath;

            var gitDir = pointer[GitDirPrefix.Length..].Trim();
            if (gitDir.Length == 0) return directoryPath;

            // Relative pointers are resolved against the checkout, which is what git itself does.
            if (!Path.IsPathRooted(gitDir))
                gitDir = Path.GetFullPath(Path.Combine(directoryPath, gitDir));

            var mainGitDir = TrimWorktreeSuffix(gitDir);

            // No worktrees segment means a submodule or some other gitdir redirection: not a second
            // checkout of one repository, so its memories are its own.
            if (mainGitDir is null) return directoryPath;

            var mainRepo = Path.GetDirectoryName(mainGitDir);
            return string.IsNullOrEmpty(mainRepo) ? directoryPath : mainRepo;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return directoryPath;
        }
    }

    /// <summary>
    /// <c>.../main/.git/worktrees/&lt;name&gt;</c> → <c>.../main/.git</c>, or null when the path holds
    /// no <c>worktrees</c> segment.
    /// </summary>
    private static string? TrimWorktreeSuffix(string gitDir)
    {
        var parts = gitDir.Split('/', '\\');
        for (var i = parts.Length - 1; i > 0; i--)
        {
            if (!string.Equals(parts[i], WorktreesSegment, StringComparison.OrdinalIgnoreCase)) continue;
            return string.Join(Path.DirectorySeparatorChar, parts[..i]);
        }
        return null;
    }
}
