namespace Eidet.Bench.Tests;

/// <summary>
/// The bundled fixture: canonical SWE-bench columns populated, ingestion ordering (related before
/// base), the base/related split conveyed structurally, and the limit contract.
/// </summary>
public class FixtureDatasetTests
{
    [Fact]
    public async Task Loads_RelatedBeforeBase_WithCanonicalColumnsPopulated()
    {
        var tasks = await new FixtureDataset().LoadAsync(0);

        Assert.Equal(5, tasks.Count);
        Assert.Equal(2, tasks.Count(t => t.IsRelated));
        // Ingestion order: every related task precedes every base task.
        Assert.Equal(tasks.OrderByDescending(t => t.IsRelated).Select(t => t.InstanceId), tasks.Select(t => t.InstanceId));

        Assert.All(tasks, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Repo));
            Assert.False(string.IsNullOrWhiteSpace(t.InstanceId));
            Assert.False(string.IsNullOrWhiteSpace(t.BaseCommit));
            Assert.False(string.IsNullOrWhiteSpace(t.Patch));
            Assert.False(string.IsNullOrWhiteSpace(t.TestPatch));
            Assert.False(string.IsNullOrWhiteSpace(t.ProblemStatement));
            Assert.False(string.IsNullOrWhiteSpace(t.CreatedAt));
            Assert.True(t.Version > 0);
            Assert.StartsWith("[", t.FailToPass); // JSON-encoded test lists, as published
            Assert.StartsWith("[", t.PassToPass);
            Assert.False(string.IsNullOrWhiteSpace(t.EnvironmentSetupCommit));
        });
    }

    [Fact]
    public async Task Limit_CapsBaseTasks_RelatedRideAlong()
    {
        var tasks = await new FixtureDataset().LoadAsync(1);

        Assert.Equal(2, tasks.Count(t => t.IsRelated));
        Assert.Equal(1, tasks.Count(t => !t.IsRelated));
    }
}
