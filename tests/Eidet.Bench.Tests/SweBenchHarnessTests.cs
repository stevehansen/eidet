using Eidet.Core.Benchmark;

namespace Eidet.Bench.Tests;

/// <summary>
/// Harness logic: phase ordering (ingest related, then evaluate base), the memory lift the
/// fixture is engineered to show, feedback wiring, scorer wiring, and the loud-failure paths.
/// </summary>
public class SweBenchHarnessTests
{
    [Fact]
    public async Task EidetArm_ResolvesWhatTheControlArmCannot()
    {
        var (solver, oracle) = await FixtureScript.ScriptedPortsAsync();

        var control = await FixtureScript.NewHarness(new NoMemoryBackend(), solver, oracle).RunAsync(0);
        var eidet = await FixtureScript.NewHarness(FixtureScript.NewEidetArm(), solver, oracle).RunAsync(0);

        // Without memory nothing resolves; with Eidet the two tasks whose related trajectories
        // carry the trigger keyword resolve, and the unrelated third honestly stays unresolved.
        Assert.Equal(0, control.Resolved);
        Assert.Equal(2, eidet.Resolved);
        Assert.Equal(3, eidet.BaseTasks);
        Assert.Equal(2, eidet.RelatedTasks);
    }

    [Fact]
    public async Task RelatedTasks_AreSeededWithEmptyContext_BaseTasksRecallAndGetFeedback()
    {
        var (solver, oracle) = await FixtureScript.ScriptedPortsAsync();
        var spy = new SpyBackend();

        await FixtureScript.NewHarness(spy, solver, oracle).RunAsync(0);

        Assert.Equal(1, spy.ResetCalls);
        // Ingestion: exactly the two related tasks, seeded from empty-context solves.
        Assert.Equal(
            new[] { "acme__parquet-tools-101", "acme__parquet-tools-102" },
            spy.Seeded.Select(o => o.Task.InstanceId));
        Assert.All(spy.Seeded, o => Assert.True(o.Context.IsEmpty));
        // Evaluation: exactly the three base tasks recall.
        Assert.Equal(
            new[] { "acme__parquet-tools-201", "acme__parquet-tools-202", "acme__parquet-tools-203" },
            spy.Recalled);
        // The spy returns a non-empty context, so every base task reports feedback; the scripted
        // context carries no trigger keyword, so nothing resolves and all feedback is negative.
        Assert.Equal(3, spy.Feedback.Count);
        Assert.All(spy.Feedback, f => Assert.False(f.WasUseful));
    }

    [Fact]
    public async Task CapabilityScorers_FillTheirRows_AndSeeEveryOutcome()
    {
        var (solver, oracle) = await FixtureScript.ScriptedPortsAsync();
        var scorer = new CapturingScorer(AmaCapability.CausalInference);

        var report = await FixtureScript
            .NewHarness(new NoMemoryBackend(), solver, oracle, [scorer])
            .RunAsync(0);

        Assert.Equal(5, scorer.SeenOutcomes!.Count); // 2 related + 3 base
        var row = Assert.Single(report.Ama);
        Assert.Equal(AmaCapability.CausalInference, row.Capability);
    }

    [Fact]
    public async Task UnavailableDataset_Throws()
    {
        var (solver, oracle) = await FixtureScript.ScriptedPortsAsync();
        var harness = new SweBenchHarness(
            new UnavailableDataset(), new NoMemoryBackend(), solver, oracle, [], TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(0));
    }

    private sealed class UnavailableDataset : ISweDatasetPort
    {
        public string Name => "gone";
        public bool IsRealDataset => true;
        public bool IsAvailable => false;
        public Task<IReadOnlyList<SweTask>> LoadAsync(int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class SpyBackend : IMemoryBackend
    {
        public int ResetCalls;
        public List<SolveOutcome> Seeded { get; } = [];
        public List<string> Recalled { get; } = [];
        public List<(string TaskId, bool WasUseful)> Feedback { get; } = [];

        public string Name => "spy";

        public Task ResetAsync(CancellationToken ct = default)
        {
            ResetCalls++;
            return Task.CompletedTask;
        }

        public Task SeedTrajectoryAsync(SolveOutcome trajectory, CancellationToken ct = default)
        {
            Seeded.Add(trajectory);
            return Task.CompletedTask;
        }

        public Task<RecalledContext> RecallAsync(SweTask task, CancellationToken ct = default)
        {
            Recalled.Add(task.InstanceId);
            return Task.FromResult(new RecalledContext(
                task.InstanceId, [new RecalledFragment("memories/spy/1", "an unhelpful fragment")]));
        }

        public Task FeedbackAsync(RecalledContext used, bool wasUseful, CancellationToken ct = default)
        {
            Feedback.Add((used.TaskInstanceId, wasUseful));
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingScorer(AmaCapability capability) : ICapabilityScorer
    {
        public IReadOnlyList<SolveOutcome>? SeenOutcomes { get; private set; }
        public AmaCapability Capability => capability;

        public CapabilityScore Score(IReadOnlyList<SolveOutcome> outcomes)
        {
            SeenOutcomes = outcomes;
            return new CapabilityScore(capability, outcomes.Count, 0.5, 0.5, 0.5, 0.5);
        }
    }
}
