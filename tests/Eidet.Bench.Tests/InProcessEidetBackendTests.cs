using Eidet.Benchmark.Tests;
using Eidet.Core.Services;

namespace Eidet.Bench.Tests;

/// <summary>
/// The in-process Eidet arm over <see cref="BenchInMemoryStore"/>: trajectories survive the real
/// write gate and come back through real recall; feedback reaches the entries; reset empties the
/// arm; and a gate rejection fails loudly instead of silently weakening the arm.
/// </summary>
public class InProcessEidetBackendTests
{
    private static (InProcessEidetBackend Backend, BenchInMemoryStore Store) NewBackend()
    {
        var store = new BenchInMemoryStore();
        var service = new MemoryService(store, new LayerService(store));
        return (new InProcessEidetBackend(service, store, "eidet-backend-tests"), store);
    }

    private static async Task<SolveOutcome> FixtureOutcomeAsync(string instanceId)
    {
        var tasks = await new FixtureDataset().LoadAsync(0);
        var task = tasks.Single(t => t.InstanceId == instanceId);
        return new SolveOutcome(
            task,
            RecalledContext.Empty(task.InstanceId),
            new SolveResult(task.Patch, 900),
            new Verdict(true, true));
    }

    [Fact]
    public async Task SeededTrajectory_IsRecalledForTheLinkedBaseTask()
    {
        var (backend, _) = NewBackend();
        await backend.SeedTrajectoryAsync(await FixtureOutcomeAsync("acme__parquet-tools-101"));

        var tasks = await new FixtureDataset().LoadAsync(0);
        var baseTask = tasks.Single(t => t.InstanceId == "acme__parquet-tools-201");
        var context = await backend.RecallAsync(baseTask);

        var fragment = Assert.Single(context.Fragments);
        Assert.Contains("acme__parquet-tools-101", fragment.Content);
        Assert.Contains("clamped", fragment.Content); // the trigger the scripted solver needs
    }

    [Fact]
    public async Task Feedback_ReachesTheStoredEntry()
    {
        var (backend, store) = NewBackend();
        await backend.SeedTrajectoryAsync(await FixtureOutcomeAsync("acme__parquet-tools-101"));

        var tasks = await new FixtureDataset().LoadAsync(0);
        var baseTask = tasks.Single(t => t.InstanceId == "acme__parquet-tools-201");
        var context = await backend.RecallAsync(baseTask);
        await backend.FeedbackAsync(context, wasUseful: true);

        var entry = await store.GetAsync(context.Fragments[0].MemoryId);
        Assert.NotNull(entry);
        Assert.Equal(1, entry.EchoCount);
    }

    [Fact]
    public async Task Reset_EmptiesTheArm()
    {
        var (backend, _) = NewBackend();
        await backend.SeedTrajectoryAsync(await FixtureOutcomeAsync("acme__parquet-tools-101"));
        await backend.SeedTrajectoryAsync(await FixtureOutcomeAsync("acme__parquet-tools-102"));

        await backend.ResetAsync();

        var tasks = await new FixtureDataset().LoadAsync(0);
        var context = await backend.RecallAsync(tasks.First(t => !t.IsRelated));
        Assert.True(context.IsEmpty);
    }

    [Fact]
    public async Task GateRejectedTrajectory_ThrowsInsteadOfSilentlyWeakeningTheArm()
    {
        var (backend, _) = NewBackend();
        var outcome = await FixtureOutcomeAsync("acme__parquet-tools-101");
        // A solve trajectory that leaked a credential must be rejected by the always-on secret
        // gate — and the backend must fail loudly rather than silently weaken the memory arm.
        var doomed = outcome with
        {
            Result = new SolveResult("+aws_access_key_id = \"AKIAIOSFODNN7EXAMPLE\"\n", 0),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.SeedTrajectoryAsync(doomed));
    }
}
