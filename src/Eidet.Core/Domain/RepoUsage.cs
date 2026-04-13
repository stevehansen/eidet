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

    public static string MakeId(string repoId) =>
        $"usage/{RepoIdNormalizer.Normalize(repoId).Replace('\\', '-').Replace('/', '-').Replace(':', '-')}";
}
