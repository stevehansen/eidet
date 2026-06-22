namespace Eidet.Core.Domain;

/// <summary>
/// Anchor document per repo for RavenDB time series.
/// Time series attached: Calls/{Operation} with values [durationMs, resultCount].
/// </summary>
public class RepoUsage
{
    public string Id { get; set; } = "";
    public string RepoId { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The original filesystem path (e.g., "P:\claude") before normalization.
    /// Used by Web UI to run intake and other path-dependent operations.
    /// </summary>
    public string? OriginalPath { get; set; }

    /// <summary>
    /// Per-repo learned lexical-vs-vector blend weight (the recall <c>alpha</c>), EWMA-updated from
    /// echo/fizzle feedback. Null = unlearned → callers fall back to <c>RecallWeights.Default.Alpha</c>.
    /// </summary>
    public double? AlphaLex { get; set; }

    /// <summary>Count of EWMA alpha updates applied — diagnostics / warmup signal.</summary>
    public long AlphaSamples { get; set; }

    public static string MakeId(string repoId) =>
        $"usage/{RepoIdNormalizer.Normalize(repoId).Replace('\\', '-').Replace('/', '-').Replace(':', '-')}";

    /// <summary>
    /// Returns true if the given string looks like a filesystem path (not already normalized).
    /// </summary>
    public static bool LooksLikePath(string value) =>
        value.Contains(':') || value.Contains('\\') || value.Contains('/');

    /// <summary>
    /// Attempts to infer the original filesystem path from a normalized repo ID.
    /// Only handles the common "X--Name" → "X:\Name" pattern on Windows.
    /// Returns null if the pattern doesn't match or the directory doesn't exist.
    /// </summary>
    public static string? TryInferPath(string normalizedRepoId)
    {
        if (string.IsNullOrEmpty(normalizedRepoId) || normalizedRepoId.Length < 4)
            return null;

        // Pattern: single letter, then "--", then folder name with no further dashes
        // e.g. "P--Eidet" → "P:\Eidet"
        if (normalizedRepoId[1] == '-' && normalizedRepoId[2] == '-'
            && char.IsLetter(normalizedRepoId[0]))
        {
            var folderPart = normalizedRepoId[3..];
            // Only handle simple single-folder names (no extra dashes that could be ambiguous)
            if (!folderPart.Contains('-'))
            {
                var candidate = $"{normalizedRepoId[0]}:\\{folderPart}";
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
