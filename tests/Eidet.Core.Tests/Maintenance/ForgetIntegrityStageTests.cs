using Eidet.Core.Domain;
using Eidet.Core.Integrity;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Services;
using Eidet.Core.Tests.Integrity;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// The nightly runtime half of the FAMA guarantee. The stage folds any auditor leak into the
/// maintenance report as an error (Affected = leak count) and reports 0/clean otherwise.
/// </summary>
public class ForgetIntegrityStageTests
{
    private const string Repo = "test-repo"; // MaintenanceContext.ForTest default

    private static MemoryEntry Forgotten(string idSuffix, string content)
    {
        var now = DateTime.UtcNow;
        return new MemoryEntry
        {
            Id = $"memories/{Repo}/insight/{idSuffix}",
            RepoId = Repo,
            Type = MemoryType.Insight,
            Content = content,
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now, ValidUntil = now },
            IsLatest = true,
            Importance = 0.6f,
        };
    }

    private static async Task<StageOutcome> RunAsync(InMemoryEidetStore store)
    {
        var svc = new MemoryService(store);
        return await svc.RunBulkAsync(async write =>
        {
            var ctx = MaintenanceContext.ForTest(store, write);
            return await new ForgetIntegrityStage().ExecuteAsync(ctx, default);
        });
    }

    [Fact]
    public async Task CleanRepo_ReportsZeroAndSucceeds()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Forgotten("forgotten", "an old fact that was forgotten cleanly"));

        var outcome = await RunAsync(store);

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.Affected);
    }

    [Fact]
    public async Task LeakingRepo_ReportsLeakCountAsError()
    {
        var stale = Forgotten("leaky", "a forgotten fact a stale L1 index still returns");
        var store = new LeakyIntegrityStore { LeakVia = ReadPath.ContextL1, LeakEntry = stale };
        await store.StoreAsync(stale);

        var outcome = await RunAsync(store);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, outcome.Affected);
        Assert.Contains("leak", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }
}
