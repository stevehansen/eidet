using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

/// <summary>Merge rejections (#39) fold into the quality dashboard as a low-severity issue, read from
/// the <c>LastMergeRejectedAt</c> stamp already on the browsed entries (no new query/collection).</summary>
public class QualityMergeRejectedTests
{
    private static MemoryEntry Insight(string id, DateTime? mergeRejectedAt = null) => new()
    {
        Id = $"memories/q/insight/{id}",
        RepoId = "q",
        Type = MemoryType.Insight,
        Content = $"a sufficiently detailed insight about the subject {id}",
        Importance = 0.6f,
        Confidence = 0.7f,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
        LastMergeRejectedAt = mergeRejectedAt,
    };

    [Fact]
    public async Task MergeRejectedStamp_SurfacesAsInfoIssue()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Insight("rejected", DateTime.UtcNow));
        await store.StoreAsync(Insight("normal"));

        var report = await new QualityService(store).AnalyzeAsync("q");

        var issue = Assert.Single(report.Issues, i => i.CheckId == "merge-rejected");
        Assert.Equal(QualitySeverity.Info, issue.Severity);
        Assert.Equal(1, issue.AffectedCount);
    }

    [Fact]
    public async Task NoRejections_NoIssue()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Insight("a"));
        await store.StoreAsync(Insight("b"));

        var report = await new QualityService(store).AnalyzeAsync("q");

        Assert.DoesNotContain(report.Issues, i => i.CheckId == "merge-rejected");
    }
}
