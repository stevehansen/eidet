using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Eidet.Core.Intake.Git;

/// <summary>
/// <see cref="IGitHistorySource"/> over the local <c>git</c> binary — the ONLY
/// subprocess-touching type on the git-intake read path, so raw diff text (the
/// secret-bearing content) enters and stays confined here. <see cref="TryCreate"/>
/// probes for a usable work tree and returns null otherwise.
/// </summary>
internal sealed partial class GitCliAdapter : IGitHistorySource
{
    // git log --format uses %x1e/%x1f (ASCII record/unit separators) so multiline bodies
    // can't be confused with field or record boundaries.
    private const char RecordSeparator = '\x1e';
    private const char FieldSeparator = '\x1f';
    private const string LogFormat = "--format=%x1e%H%x1f%P%x1f%ae%x1f%aI%x1f%s%x1f%b%x1f";

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private readonly string _repoPath;

    private GitCliAdapter(string repoPath) => _repoPath = repoPath;

    public bool IsAvailable => true;

    /// <summary>Null when the path is not inside a git work tree or git is not runnable.</summary>
    public static GitCliAdapter? TryCreate(string projectPath)
    {
        if (!Directory.Exists(projectPath)) return null;
        try
        {
            var probe = RunGitSync(projectPath, ["rev-parse", "--is-inside-work-tree"]);
            return probe.ExitCode == 0 && probe.Stdout.Trim() == "true" ? new GitCliAdapter(projectPath) : null;
        }
        catch
        {
            return null;
        }
    }

    public async IAsyncEnumerable<CommitRecord> ReadMergedHistoryAsync(
        GitHistoryQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // `since` reaches a subprocess argument — accept only hex SHAs (blocks option/revision
        // injection through the watermark or the --since CLI flag).
        if (query.Since is not null && !ShaRegex().IsMatch(query.Since))
            throw new ArgumentException($"'{query.Since}' is not a commit SHA.", nameof(query));

        var args = new List<string> { "log", "--no-color", "--numstat", $"--max-count={query.MaxCommits}", LogFormat };
        if (query.MergesOnly) args.Add("--merges");
        if (query.Since is not null) args.Add($"{query.Since}..HEAD");

        var result = await RunGitAsync(args, ct);
        if (result.ExitCode != 0 && query.Since is not null)
        {
            // The watermark SHA can vanish (history rewrite, gc). Fall back to the plain
            // bounded log — content-hash dedup makes re-processing safe.
            result = await RunGitAsync(args[..^1], ct);
        }
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git log failed: {result.Stderr.Trim()}");

        foreach (var commit in ParseLog(result.Stdout))
        {
            ct.ThrowIfCancellationRequested();
            yield return commit;
        }
    }

    public async IAsyncEnumerable<DiffHunk> ReadDiffAsync(
        string sha, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!ShaRegex().IsMatch(sha))
            throw new ArgumentException($"'{sha}' is not a commit SHA.", nameof(sha));

        var result = await RunGitAsync(["show", sha, "--format=", "--no-color", "--unified=0"], ct);
        if (result.ExitCode != 0) yield break; // merge commit / unknown sha → no hunks

        foreach (var hunk in ParseDiff(result.Stdout))
        {
            ct.ThrowIfCancellationRequested();
            yield return hunk;
        }
    }

    /// <summary>Parse `git log` output produced with <see cref="LogFormat"/> + <c>--numstat</c>.</summary>
    internal static List<CommitRecord> ParseLog(string stdout)
    {
        var commits = new List<CommitRecord>();
        foreach (var record in stdout.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // Fields: sha, parents, author email, ISO date, subject, body, trailing numstat block.
            var parts = record.Split(FieldSeparator);
            if (parts.Length < 7) continue;
            var sha = parts[0].Trim();
            if (sha.Length == 0) continue;

            commits.Add(new CommitRecord(
                Sha: sha,
                Subject: parts[4].Trim(),
                Body: parts[5].Trim(),
                AuthorEmail: parts[2].Trim(),
                CommittedAt: DateTimeOffset.TryParse(parts[3], out var at) ? at : default,
                IsMerge: parts[1].Trim().Contains(' '),
                Files: ParseNumstat(parts[6])));
        }
        return commits;
    }

    private static List<FileChange> ParseNumstat(string block)
    {
        var files = new List<FileChange>();
        foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cols = line.Split('\t');
            if (cols.Length < 3) continue;
            _ = int.TryParse(cols[0], out var added);   // "-" (binary) → 0
            _ = int.TryParse(cols[1], out var removed);
            var path = cols[2];
            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
                path = path[1..^1]; // git quotes paths with spaces/non-ASCII
            // numstat carries no status letter; renames are recognizable by "=>", the rest
            // reports as Modified — Kind is informational, not load-bearing for mining.
            var kind = path.Contains("=>", StringComparison.Ordinal) ? ChangeKind.Renamed : ChangeKind.Modified;
            files.Add(new FileChange(path, added, removed, kind));
        }
        return files;
    }

    /// <summary>Parse unified-diff output of `git show` into hunks.</summary>
    internal static List<DiffHunk> ParseDiff(string stdout)
    {
        const int maxHunks = 200;
        var hunks = new List<DiffHunk>();
        string? oldPath = null;
        string? currentPath = null;
        string? header = null;
        List<string>? lines = null;

        void Flush()
        {
            if (header is not null && currentPath is not null)
                hunks.Add(new DiffHunk(currentPath, header, lines ?? []));
            header = null;
            lines = null;
        }

        foreach (var raw in stdout.Split('\n'))
        {
            if (hunks.Count >= maxHunks) break;
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git", StringComparison.Ordinal))
            {
                Flush();
                oldPath = null;
                currentPath = null;
            }
            else if (line.StartsWith("--- a/", StringComparison.Ordinal))
            {
                oldPath = line[6..];
            }
            else if (line.StartsWith("--- \"a/", StringComparison.Ordinal))
            {
                oldPath = line[7..^1]; // git quotes paths with spaces/non-ASCII: --- "a/…"
            }
            else if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                currentPath = line[6..];
            }
            else if (line.StartsWith("+++ \"b/", StringComparison.Ordinal))
            {
                currentPath = line[7..^1];
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                currentPath = oldPath; // deleted file: "+++ /dev/null"
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                header = line;
                lines = [];
            }
            else if (lines is not null && line.Length > 0 && (line[0] == '+' || line[0] == '-'))
            {
                lines.Add(line);
            }
        }
        Flush();
        return hunks;
    }

    private async Task<GitResult> RunGitAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        using var process = StartGit(_repoPath, args);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }
        return new GitResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static GitResult RunGitSync(string workingDirectory, IReadOnlyList<string> args)
    {
        using var process = StartGit(workingDirectory, args);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            return new GitResult(-1, "", "git timed out");
        }
        return new GitResult(process.ExitCode, stdout, stderr);
    }

    private static Process StartGit(string workingDirectory, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
    }

    private readonly record struct GitResult(int ExitCode, string Stdout, string Stderr);

    [GeneratedRegex("^[0-9a-fA-F]{4,64}$")]
    private static partial Regex ShaRegex();
}
