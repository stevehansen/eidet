using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class UsageTrackerTests
{
    // ─── RepoUsage.MakeId ────────────────────────────────────────

    [Fact]
    public void MakeId_WindowsPath_NormalizesSlashes()
    {
        var id = RepoUsage.MakeId(@"P:\Eidet");
        Assert.StartsWith("usage/", id);
        Assert.DoesNotContain("\\", id);
        Assert.DoesNotContain(":", id);
    }

    [Fact]
    public void MakeId_UnixPath_NormalizesSlashes()
    {
        var id = RepoUsage.MakeId("/home/user/project");
        Assert.StartsWith("usage/", id);
        Assert.DoesNotContain("\\", id);
    }

    [Fact]
    public void MakeId_SamePath_SameId()
    {
        var id1 = RepoUsage.MakeId(@"P:\Eidet");
        var id2 = RepoUsage.MakeId(@"P:\Eidet");
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void MakeId_DifferentPaths_DifferentIds()
    {
        var id1 = RepoUsage.MakeId(@"P:\Eidet");
        var id2 = RepoUsage.MakeId(@"P:\OtherProject");
        Assert.NotEqual(id1, id2);
    }

    // ─── NullUsageTracker ────────────────────────────────────────

    [Fact]
    public async Task NullTracker_RecordDoesNotThrow()
    {
        await NullUsageTracker.Instance.RecordAsync("test", "Store", 42.0, 1);
    }

    [Fact]
    public async Task NullTracker_GetUsageReturnsEmptyReport()
    {
        var report = await NullUsageTracker.Instance.GetUsageAsync("test");
        Assert.NotNull(report);
        Assert.Equal("test", report.RepoId);
        Assert.Empty(report.Operations);
        Assert.Equal(0, report.TotalCalls);
    }

    [Fact]
    public async Task NullTracker_GetTimeSeriesReturnsEmpty()
    {
        var data = await NullUsageTracker.Instance.GetTimeSeriesAsync("test", "Store");
        Assert.NotNull(data);
        Assert.Empty(data);
    }

    [Fact]
    public async Task NullTracker_GetHourlyReturnsEmpty()
    {
        var buckets = await NullUsageTracker.Instance.GetHourlyBreakdownAsync("test");
        Assert.NotNull(buckets);
        Assert.Empty(buckets);
    }

    [Fact]
    public void NullTracker_StartScopeDoesNotThrow()
    {
        using var scope = NullUsageTracker.Instance.StartScope("test", "Store");
        scope.SetResultCount(5);
        // Should not throw on dispose
    }

    // ─── UsageScope ──────────────────────────────────────────────

    [Fact]
    public void UsageScope_DoubleDispose_DoesNotThrow()
    {
        var scope = NullUsageTracker.Instance.StartScope("test", "Recall");
        scope.Dispose();
        scope.Dispose(); // Should not throw
    }

    // ─── Report models ──────────────────────────────────────────

    [Fact]
    public void UsageReport_DefaultsToEmpty()
    {
        var report = new UsageReport();
        Assert.Equal("", report.RepoId);
        Assert.Equal(0, report.TotalCalls);
        Assert.Empty(report.Operations);
    }

    [Fact]
    public void OperationStats_DefaultValues()
    {
        var stats = new OperationStats();
        Assert.Equal("", stats.Operation);
        Assert.Equal(0, stats.CallCount);
        Assert.Equal(0, stats.TotalDurationMs);
        Assert.Equal(0, stats.TotalResults);
    }

    [Fact]
    public void HourlyBucket_DefaultValues()
    {
        var bucket = new HourlyBucket();
        Assert.Equal(0, bucket.TotalCalls);
        Assert.Empty(bucket.ByOperation);
    }

    [Fact]
    public void UsageDataPoint_DefaultValues()
    {
        var point = new UsageDataPoint();
        Assert.Equal(0, point.DurationMs);
        Assert.Equal(0, point.ResultCount);
    }
}
