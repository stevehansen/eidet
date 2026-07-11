using System.Text;
using System.Text.RegularExpressions;
using Eidet.Core.Domain;

namespace Eidet.Core.Intake.Git;

/// <summary>
/// Mines merged commit history into seed memories: the problem from the commit message, the
/// fix PATTERN from change stats and hunk-header regions — never raw diff lines. Deterministic
/// gate (zero-LLM): Conventional-Commits allowlist (<c>fix</c> → Procedure; <c>feat</c>/<c>perf</c>/
/// <c>refactor</c>/<c>arch</c> → Insight), described merge commits, ADR markers; everything else
/// is skipped with a per-commit reason so "0 new" is never mysterious. Active only when
/// <see cref="IntakeOptions.Git"/> is set, so whole-repo file intake never rides into git history.
/// </summary>
public sealed partial class GitHistoryExtractor : IIntakeExtractor
{
    private const int MaxFilesPerCommit = 25;
    private const int MaxFilesInPattern = 8;
    private const int MaxRegions = 5;
    private const int MaxHunksScanned = 40;
    private const int MaxBodyChars = 600;

    private readonly IGitHistorySource _git;

    public GitHistoryExtractor(IGitHistorySource git) => _git = git;

    /// <summary>The underlying port — lets <c>IntakeService</c> read the repo tip for the watermark.</summary>
    internal IGitHistorySource Source => _git;

    public string Name => "git.history";

    public bool AppliesTo(IntakeContext ctx) => _git.IsAvailable && ctx.Options.Git is not null;

    public async Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct)
    {
        var opts = ctx.Options.Git!;
        var query = new GitHistoryQuery(opts.MaxCommits, opts.Since);
        await foreach (var commit in _git.ReadMergedHistoryAsync(query, ct))
        {
            var source = $"commit {ShortSha(commit.Sha)}";
            try
            {
                var verdict = Gate(commit, opts);
                if (verdict.SkipReason is not null)
                {
                    sink.RecordSkipped(source, verdict.SkipReason);
                    continue;
                }
                await sink.AddMemoryAsync(await MineAsync(commit, verdict, source, ct), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable commit must not sink the batch.
                sink.RecordSkipped(source, $"error: {ex.Message}");
            }
        }
    }

    /// <summary>Deterministic "valuable enough" gate. Returns either a skip reason or the memory shape.</summary>
    private static GateVerdict Gate(CommitRecord commit, GitIntakeOptions opts)
    {
        if (!commit.IsMerge && commit.Files.Count == 0)
            return GateVerdict.Skip("no file changes");
        if (commit.Files.Count > MaxFilesPerCommit)
            return GateVerdict.Skip($"touches {commit.Files.Count} files (bulk change)");

        var conventional = ConventionalSubjectRegex().Match(commit.Subject);
        if (conventional.Success)
        {
            var type = conventional.Groups["type"].Value.ToLowerInvariant();
            var scope = conventional.Groups["scope"].Value is { Length: > 0 } s ? s : null;
            return type switch
            {
                "fix" => GateVerdict.Mine(MemoryType.Procedure, 0.6f, type, scope),
                "feat" or "perf" or "refactor" or "arch" => GateVerdict.Mine(MemoryType.Insight, 0.5f, type, scope),
                _ => GateVerdict.Skip($"commit type '{type}' not mined"),
            };
        }

        if (AdrMarkerRegex().IsMatch(commit.Subject))
            return GateVerdict.Mine(MemoryType.Insight, 0.6f, null, null);

        if (commit.IsMerge)
        {
            var described = commit.Body.Trim().Length > 0 ||
                            commit.Subject.StartsWith("Merge pull request", StringComparison.OrdinalIgnoreCase);
            return described
                ? GateVerdict.Mine(MemoryType.Insight, 0.5f, null, null)
                : GateVerdict.Skip("merge commit without description");
        }

        if (!opts.IncludeNonConventional)
            return GateVerdict.Skip("non-conventional commit message");

        return FixWordRegex().IsMatch(commit.Subject)
            ? GateVerdict.Mine(MemoryType.Procedure, 0.4f, null, null)
            : GateVerdict.Mine(MemoryType.Insight, 0.4f, null, null);
    }

    private async Task<IntakeMemory> MineAsync(
        CommitRecord commit, GateVerdict verdict, string source, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(commit.Subject.Trim());

        var body = commit.Body.Trim();
        if (body.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(StringUtils.Truncate(body, MaxBodyChars));
        }

        if (commit.Files.Count > 0)
        {
            var label = verdict.Type == MemoryType.Procedure ? "Fix pattern" : "Change pattern";
            var stats = string.Join(", ",
                commit.Files.Take(MaxFilesInPattern).Select(f => $"{f.Path} (+{f.Added}/-{f.Removed})"));
            var more = commit.Files.Count > MaxFilesInPattern
                ? $" and {commit.Files.Count - MaxFilesInPattern} more"
                : "";
            sb.AppendLine();
            sb.AppendLine($"{label}: {stats}{more}");

            var regions = await ReadRegionsAsync(commit.Sha, ct);
            if (regions.Count > 0)
                sb.AppendLine($"Regions: {string.Join("; ", regions)}");
        }

        // The SHA trailer is the idempotency key: it makes the content — and therefore the
        // orchestrator's content-hash id — unique per commit, so re-runs dedup as "duplicate".
        sb.AppendLine();
        sb.Append($"commit:{commit.Sha}");

        var tags = new List<string> { "git-intake", $"commit:{commit.Sha}" };
        if (verdict.ConventionalType is not null) tags.Add(verdict.ConventionalType);
        if (verdict.Scope is not null) tags.Add(verdict.Scope.ToLowerInvariant());
        foreach (var file in commit.Files.Take(MaxFilesInPattern))
            tags.AddRange(MarkdownIntake.TagsFromFileName(file.Path));

        return new IntakeMemory(source, verdict.Type, sb.ToString(), tags.Distinct().ToList(), verdict.Importance);
    }

    /// <summary>
    /// Lazily reads the gated commit's diff and mines only the hunk-header context (the
    /// enclosing declaration git prints after <c>@@ … @@</c>). Raw ± lines are never touched.
    /// </summary>
    private async Task<List<string>> ReadRegionsAsync(string sha, CancellationToken ct)
    {
        var regions = new List<string>();
        var scanned = 0;
        await foreach (var hunk in _git.ReadDiffAsync(sha, ct))
        {
            if (++scanned > MaxHunksScanned) break;
            var at = hunk.Header.LastIndexOf("@@", StringComparison.Ordinal);
            if (at < 0) continue;
            var context = hunk.Header[(at + 2)..].Trim();
            if (context.Length == 0 || regions.Contains(context)) continue;
            regions.Add(context);
            if (regions.Count >= MaxRegions) break;
        }
        return regions;
    }

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private readonly record struct GateVerdict(
        MemoryType Type, float Importance, string? ConventionalType, string? Scope, string? SkipReason)
    {
        public static GateVerdict Skip(string reason) => new(default, 0f, null, null, reason);

        public static GateVerdict Mine(MemoryType type, float importance, string? conventionalType, string? scope) =>
            new(type, importance, conventionalType, scope, null);
    }

    [GeneratedRegex(@"^(?<type>[A-Za-z]+)(?:\((?<scope>[^)]*)\))?!?:\s+\S")]
    private static partial Regex ConventionalSubjectRegex();

    [GeneratedRegex(@"\bADR[- ]?\d+", RegexOptions.IgnoreCase)]
    private static partial Regex AdrMarkerRegex();

    [GeneratedRegex(@"\b(fix|fixes|fixed|bug)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FixWordRegex();
}
