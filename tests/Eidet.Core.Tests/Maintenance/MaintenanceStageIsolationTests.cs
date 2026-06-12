using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Per-stage isolation tests for issue #22's new seam: a single stage is driven directly via
/// <see cref="MaintenanceContext.ForTest"/>, WITHOUT the orchestrator. These exercise the branch
/// logic inside each stage that was previously only reachable end-to-end.
///
/// Fixtures are stored directly through <see cref="InMemoryEidetStore"/> (not svc.StoreAsync) so
/// tests can set ForgetAfter / LastAccessedAt / Source, which the validated store path won't allow.
/// All fixtures use RepoId "test-repo" (ForTest's default) and IsLatest+null ValidUntil so the
/// stages' GetTopScoredAsync sweep picks them up.
/// </summary>
public class MaintenanceStageIsolationTests
{
    private const string Repo = "test-repo";

    private static MemoryEntry Entry(string id, MemoryType type, DateTime createdAt) => new()
    {
        Id = $"memories/{Repo}/{type.ToString().ToLowerInvariant()}/{id}",
        RepoId = Repo,
        Type = type,
        Content = $"content for {id}",
        CreatedAt = createdAt,
        Validity = new Validity { ValidFrom = createdAt },
        IsLatest = true,
        Importance = 0.6f,
    };

    /// <summary>Runs one stage inside a real bulk-write scope and returns its outcome.</summary>
    private static async Task<StageOutcome> RunStageAsync(InMemoryEidetStore store, IMaintenanceStage stage)
    {
        var svc = new MemoryService(store);
        return await svc.RunBulkAsync(async write =>
        {
            var ctx = MaintenanceContext.ForTest(store, write);
            return await stage.ExecuteAsync(ctx, default);
        });
    }

    // ─── ObservationRetentionStage: the grace-window branch ──────────────────
    //
    // An observation past the retention cutoff is only expired if its last touch is ALSO past the
    // grace window (ObservationRetentionDays / 2). A recently-touched old observation is spared.

    [Fact]
    public async Task ObservationRetention_OldButTouchedWithinGraceWindow_NotExpired()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        // Created 200d ago (> 90d cutoff) but accessed 10d ago (< 45d grace window) → spared.
        var obs = Entry("recently-touched", MemoryType.Observation, now.AddDays(-200));
        obs.LastAccessedAt = now.AddDays(-10);
        await store.StoreAsync(obs);

        var outcome = await RunStageAsync(store, new ObservationRetentionStage());

        Assert.Equal(0, outcome.Affected);
        Assert.Null(obs.Validity.ValidUntil);
        Assert.Null(obs.ForgetReason);
    }

    [Fact]
    public async Task ObservationRetention_OldAndUntouchedPastGraceWindow_Expired()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        // Created 200d ago and last accessed 100d ago (> 45d grace window) → expired.
        var obs = Entry("stale", MemoryType.Observation, now.AddDays(-200));
        obs.LastAccessedAt = now.AddDays(-100);
        await store.StoreAsync(obs);

        var outcome = await RunStageAsync(store, new ObservationRetentionStage());

        Assert.Equal(1, outcome.Affected);
        Assert.NotNull(obs.Validity.ValidUntil);
        Assert.Contains("Observation retention", obs.ForgetReason);
    }

    [Fact]
    public async Task ObservationRetention_NullLastAccessed_FallsBackToCreatedAt_Expired()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        // Never accessed → grace window measured from CreatedAt (200d ago) → past it → expired.
        var obs = Entry("never-touched", MemoryType.Observation, now.AddDays(-200));
        await store.StoreAsync(obs);

        var outcome = await RunStageAsync(store, new ObservationRetentionStage());

        Assert.Equal(1, outcome.Affected);
        Assert.NotNull(obs.Validity.ValidUntil);
    }

    // ─── OrphanCleanupStage: the two orphan predicates ───────────────────────

    [Fact]
    public async Task OrphanCleanup_EmptyContent_IsCleaned()
    {
        var store = new InMemoryEidetStore();
        var empty = Entry("blank", MemoryType.Insight, DateTime.UtcNow);
        empty.Content = "   "; // whitespace-only → IsNullOrWhiteSpace
        await store.StoreAsync(empty);

        var outcome = await RunStageAsync(store, new OrphanCleanupStage());

        Assert.Equal(1, outcome.Affected);
        Assert.NotNull(empty.Validity.ValidUntil);
        Assert.Equal("Orphan cleanup", empty.ForgetReason);
    }

    [Fact]
    public async Task OrphanCleanup_SystemLowImportanceAged_IsCleaned()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        // Source=="system" AND Importance<=0.1 AND age>30d → orphan.
        var sys = Entry("sys", MemoryType.Insight, now.AddDays(-40));
        sys.Source = "system";
        sys.Importance = 0.05f;
        await store.StoreAsync(sys);

        var outcome = await RunStageAsync(store, new OrphanCleanupStage());

        Assert.Equal(1, outcome.Affected);
        Assert.NotNull(sys.Validity.ValidUntil);
        Assert.Equal("Orphan cleanup", sys.ForgetReason);
    }

    [Fact]
    public async Task OrphanCleanup_NormalEntry_IsUntouched()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        // Non-empty content; even though aged + low importance, Source is not "system".
        var normal = Entry("keep", MemoryType.Insight, now.AddDays(-40));
        normal.Importance = 0.05f;
        await store.StoreAsync(normal);

        var outcome = await RunStageAsync(store, new OrphanCleanupStage());

        Assert.Equal(0, outcome.Affected);
        Assert.Null(normal.Validity.ValidUntil);
        Assert.Null(normal.ForgetReason);
    }

    // ─── TtlExpiryStage: ForgetAfter relative to Now ─────────────────────────

    [Fact]
    public async Task TtlExpiry_PastDueForgetAfter_Expired()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        var entry = Entry("expired", MemoryType.Insight, now.AddDays(-5));
        entry.ForgetAfter = now.AddDays(-1); // <= Now
        await store.StoreAsync(entry);

        var outcome = await RunStageAsync(store, new TtlExpiryStage());

        Assert.Equal(1, outcome.Affected);
        Assert.NotNull(entry.Validity.ValidUntil);
        Assert.Equal("TTL expired", entry.ForgetReason);
    }

    [Fact]
    public async Task TtlExpiry_FutureForgetAfter_Untouched()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        var entry = Entry("keep", MemoryType.Insight, now.AddDays(-5));
        entry.ForgetAfter = now.AddDays(30); // in the future
        await store.StoreAsync(entry);

        var outcome = await RunStageAsync(store, new TtlExpiryStage());

        Assert.Equal(0, outcome.Affected);
        Assert.Null(entry.Validity.ValidUntil);
        Assert.Null(entry.ForgetReason);
    }

    [Fact]
    public async Task TtlExpiry_NoForgetAfter_Untouched()
    {
        var store = new InMemoryEidetStore();
        var entry = Entry("no-ttl", MemoryType.Insight, DateTime.UtcNow.AddDays(-5));
        await store.StoreAsync(entry); // ForgetAfter stays null

        var outcome = await RunStageAsync(store, new TtlExpiryStage());

        Assert.Equal(0, outcome.Affected);
        Assert.Null(entry.Validity.ValidUntil);
    }
}
