using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

/// <summary>
/// Recall-side drift surfacing: a Stale/Contradicted verdict replaces the age-based
/// staleness warning with "[drift: {reason}]"; Ok/Vague verdicts leave the age-based
/// behavior untouched.
/// </summary>
public class MemoryServiceDriftWarningTests
{
    private static MemoryEntry MakeEntry(DateTime createdAt, DriftReview? drift) => new()
    {
        Id = "memories/repo-a/insight/drift-recall-1",
        RepoId = "repo-a",
        Type = MemoryType.Insight,
        Content = "deployment uses argo cd via gitops",
        CreatedAt = createdAt,
        Validity = new Validity { ValidFrom = createdAt },
        IsLatest = true,
        Importance = 0.7f,
        Drift = drift,
    };

    private static async Task<MemorySearchResult> RecallSingleAsync(MemoryEntry entry)
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(entry);
        var svc = new MemoryService(store);
        var results = await svc.RecallAsync("repo-a", "deployment");
        return Assert.Single(results);
    }

    [Theory]
    [InlineData(DriftVerdictKind.Stale)]
    [InlineData(DriftVerdictKind.Contradicted)]
    public async Task Recall_DriftFlaggedEntry_WarnsWithDriftReason_OverAgeMessage(DriftVerdictKind verdict)
    {
        // 30 days old — the age-based warning (>= 7d default) would fire too; drift must win.
        var entry = MakeEntry(DateTime.UtcNow.AddDays(-30), new DriftReview
        {
            Verdict = verdict,
            ModelConfidence = 0.9f,
            Reason = "search moved to Corax",
            ReviewedAt = DateTime.UtcNow,
        });

        var result = await RecallSingleAsync(entry);

        Assert.Equal("[drift: search moved to Corax]", result.StalenessWarning);
        Assert.Equal(verdict, result.Drift!.Verdict); // verdict travels on the search result
    }

    [Fact]
    public async Task Recall_DriftFlaggedEntryWithoutReason_FallsBackToVerdictName()
    {
        var entry = MakeEntry(DateTime.UtcNow.AddDays(-30), new DriftReview
        {
            Verdict = DriftVerdictKind.Stale,
            ModelConfidence = 0.9f,
            Reason = null,
            ReviewedAt = DateTime.UtcNow,
        });

        var result = await RecallSingleAsync(entry);

        Assert.Equal("[drift: stale]", result.StalenessWarning);
    }

    [Theory]
    [InlineData(DriftVerdictKind.Ok)]
    [InlineData(DriftVerdictKind.Vague)]
    public async Task Recall_OkOrVagueDrift_KeepsAgeBasedWarning(DriftVerdictKind verdict)
    {
        var entry = MakeEntry(DateTime.UtcNow.AddDays(-30), new DriftReview
        {
            Verdict = verdict,
            ModelConfidence = 0.9f,
            Reason = "should not surface",
            ReviewedAt = DateTime.UtcNow,
        });

        var result = await RecallSingleAsync(entry);

        Assert.Equal($"[stale: {result.AgeDays}d ago — verify before acting]", result.StalenessWarning);
    }

    [Theory]
    [InlineData(DriftVerdictKind.Ok)]
    [InlineData(DriftVerdictKind.Vague)]
    public async Task Recall_FreshEntryWithOkOrVagueDrift_HasNoWarning(DriftVerdictKind verdict)
    {
        var entry = MakeEntry(DateTime.UtcNow.AddDays(-1), new DriftReview
        {
            Verdict = verdict,
            ModelConfidence = 0.9f,
            Reason = "should not surface",
            ReviewedAt = DateTime.UtcNow,
        });

        var result = await RecallSingleAsync(entry);

        Assert.Null(result.StalenessWarning);
    }
}
