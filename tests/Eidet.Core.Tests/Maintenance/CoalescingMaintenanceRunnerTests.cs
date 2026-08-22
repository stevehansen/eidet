using Eidet.Core.Maintenance;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// The authority on "one pipeline execution per repo at a time". Two concurrent passes over one
/// repo interleave their document rewrites, so overlap has to be impossible rather than unlikely —
/// see <see cref="SharedEntryStore"/> for the same failure inside a single run.
/// </summary>
public class CoalescingMaintenanceRunnerTests
{
    [Fact]
    public async Task ConcurrentCallsForOneRepo_ShareASingleRun()
    {
        var inner = new BlockingRunner();
        var runner = new CoalescingMaintenanceRunner(inner);

        var first = runner.RunAsync("P:\\Repo");
        var second = runner.RunAsync("P:\\Repo");

        Assert.Same(first, second);
        inner.Release();
        Assert.Equal("P--Repo", (await first).RepoId);
        Assert.Equal(1, inner.Starts);
    }

    [Fact]
    public async Task PathAndNormalizedId_AreTheSameRepo()
    {
        var inner = new BlockingRunner();
        var runner = new CoalescingMaintenanceRunner(inner);

        var byPath = runner.RunAsync("P:\\Repo");
        var byId = runner.RunAsync("P--Repo");

        Assert.Same(byPath, byId);
        inner.Release();
        await byPath;
        Assert.Equal(1, inner.Starts);
    }

    [Fact]
    public async Task DifferentRepos_RunIndependently()
    {
        var inner = new BlockingRunner();
        var runner = new CoalescingMaintenanceRunner(inner);

        var one = runner.RunAsync("P:\\One");
        var two = runner.RunAsync("P:\\Two");

        Assert.NotSame(one, two);
        inner.Release();
        await Task.WhenAll(one, two);
        Assert.Equal(2, inner.Starts);
    }

    [Fact]
    public async Task AFinishedRun_IsNotHandedToTheNextCaller()
    {
        var inner = new BlockingRunner();
        var runner = new CoalescingMaintenanceRunner(inner);

        var first = runner.RunAsync("P:\\Repo");
        inner.Release();
        await first;

        var second = runner.RunAsync("P:\\Repo");

        Assert.NotSame(first, second);
        inner.Release();
        await second;
        Assert.Equal(2, inner.Starts);
    }

    /// <summary>
    /// A request carries a stage subset and retention overrides, so two of them are not
    /// interchangeable — coalescing them would silently give a caller someone else's pass.
    /// </summary>
    [Fact]
    public async Task RequestCalls_AreNotCoalesced()
    {
        var inner = new BlockingRunner();
        var runner = new CoalescingMaintenanceRunner(inner);

        var full = runner.RunAsync(new MaintenanceRequest { RepoId = "P--Repo" });
        var subset = runner.RunAsync(new MaintenanceRequest
        {
            RepoId = "P--Repo",
            OnlyStages = new HashSet<MaintenanceStep> { MaintenanceStep.CorpusRepair },
        });

        Assert.NotSame(full, subset);
        inner.Release();
        await Task.WhenAll(full, subset);
        Assert.Equal(2, inner.Starts);
    }

    /// <summary>Starts a run that stays in flight until <see cref="Release"/> is called.</summary>
    private sealed class BlockingRunner : IMaintenanceRunner
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Starts;

        public void Release() => _gate.TrySetResult();

        public Task<MaintenanceReport> RunAsync(string repoPathOrId, CancellationToken ct = default) =>
            RunAsync(new MaintenanceRequest { RepoId = repoPathOrId }, ct);

        public async Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Starts);
            await _gate.Task;
            return new MaintenanceReport { RepoId = request.RepoId };
        }
    }
}
