using Eidet.Core.Domain;
using Eidet.Core.Integrity;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Integrity;

/// <summary>
/// Runtime post-forget verification (#37) — the runtime half of the FAMA guarantee. Reuses the FAMA
/// per-memory predicate (a forgotten/superseded memory must be absent from every read path) over
/// sampled real memories, broadened to the two paths <c>FamaForgetTests</c> does not exercise
/// (GraphNeighbor, DuplicateDetection). Includes the coverage guard that every <see cref="ReadPath"/>
/// value is probed.
/// </summary>
public class IntegrityAuditorTests
{
    private static readonly string Repo = RepoIdNormalizer.Normalize("audit-repo");

    private static MemoryEntry Mem(string idSuffix, string content, bool forgotten = false, bool superseded = false)
    {
        var now = DateTime.UtcNow;
        return new MemoryEntry
        {
            Id = $"memories/{Repo}/insight/{idSuffix}",
            RepoId = Repo,
            Type = MemoryType.Insight,
            Content = content,
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now, ValidUntil = forgotten || superseded ? now : null },
            IsLatest = !superseded,
            Importance = 0.7f,
        };
    }

    private static IntegrityAuditor AuditorFor(InMemoryEidetStore store) =>
        new(new MemoryService(store), store);

    [Fact]
    public async Task CleanStore_NoLeaks_AndProbesEveryReadPath()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Mem("live", "embedded ravendb is the zero setup default storage mode"));
        await store.StoreAsync(Mem("forgotten", "old forgotten fact about storage modes", forgotten: true));
        await store.StoreAsync(Mem("superseded", "outdated superseded fact about storage modes", superseded: true));

        var report = await AuditorFor(store).VerifyForgottenAsync("audit-repo");

        Assert.True(report.Clean, string.Join("; ", report.Leaks));
        Assert.Equal(2, report.MemoriesProbed);            // forgotten + superseded
        // Coverage guard: every read path was dispatched. A new ReadPath value with no probe throws
        // in ProbeAsync's switch, failing this test until a probe is added.
        Assert.Equal(
            Enum.GetValues<ReadPath>().OrderBy(p => p),
            report.PathsProbed.OrderBy(p => p));
    }

    [Fact]
    public async Task EmptyStore_ProbesNothing_IsClean()
    {
        var report = await AuditorFor(new InMemoryEidetStore()).VerifyForgottenAsync("audit-repo");
        Assert.True(report.Clean);
        Assert.Equal(0, report.MemoriesProbed);
    }

    [Fact]
    public async Task DetectsContextL1Leak()
    {
        var stale = Mem("leaky", "a forgotten insight that a stale L1 index still returns", forgotten: true);
        var store = new LeakyIntegrityStore { LeakVia = ReadPath.ContextL1, LeakEntry = stale };
        await store.StoreAsync(stale);

        var report = await AuditorFor(store).VerifyForgottenAsync("audit-repo");

        Assert.False(report.Clean);
        Assert.Contains(report.Leaks, l => l.Path == ReadPath.ContextL1 && l.MemoryId == stale.Id);
    }

    [Fact]
    public async Task DetectsRecallLeak()
    {
        var stale = Mem("leaky", "a forgotten insight a stale recall arm still surfaces", forgotten: true);
        var store = new LeakyIntegrityStore { LeakVia = ReadPath.Recall, LeakEntry = stale };
        await store.StoreAsync(stale);

        var report = await AuditorFor(store).VerifyForgottenAsync("audit-repo");

        Assert.False(report.Clean);
        Assert.Contains(report.Leaks, l => l.Path == ReadPath.Recall && l.MemoryId == stale.Id);
    }

    [Fact]
    public async Task DetectsDuplicateDetectionLeak()
    {
        var stale = Mem("leaky", "a forgotten insight near-duplicate search still matches", forgotten: true);
        var store = new LeakyIntegrityStore { LeakVia = ReadPath.DuplicateDetection, LeakEntry = stale };
        await store.StoreAsync(stale);

        var report = await AuditorFor(store).VerifyForgottenAsync("audit-repo");

        Assert.False(report.Clean);
        Assert.Contains(report.Leaks, l => l.Path == ReadPath.DuplicateDetection && l.MemoryId == stale.Id);
    }

    [Fact]
    public async Task GraphNeighbor_ForgottenMemoryLinkedFromLiveParent_DoesNotResurface()
    {
        // Regression guard for the fix: forget stamps ValidUntil but leaves IsLatest=true, so an
        // IsLatest-only neighbor admission would resurface a forgotten memory reachable via a link.
        var store = new InMemoryEidetStore();

        var forgotten = Mem("forgotten-target", "the graph neighbor target fact about caching", forgotten: true);
        await store.StoreAsync(forgotten);

        var parent = Mem("live-parent", "the graph neighbor parent describes caching layers");
        parent.Links.Add(new MemoryLink { TargetRepoId = Repo, TargetMemoryId = forgotten.Id, Relation = "related" });
        await store.StoreAsync(parent);

        var svc = new MemoryService(store);
        var recalled = await svc.RecallAsync(Repo, new RecallOptions("graph neighbor caching") { ExpandGraph = true, CrossRepo = false });

        Assert.Contains(recalled, r => r.Id == parent.Id);          // the live parent surfaces
        Assert.DoesNotContain(recalled, r => r.Id == forgotten.Id); // the forgotten neighbor does NOT

        var report = await AuditorFor(store).VerifyForgottenAsync("audit-repo");
        Assert.DoesNotContain(report.Leaks, l => l.Path == ReadPath.GraphNeighbor);
    }
}

/// <summary>Folds a forget leak into the quality dashboard as a Critical issue (zero new UI plumbing).</summary>
public class QualityForgetLeakTests
{
    [Fact]
    public async Task ForgetLeak_SurfacesAsCriticalQualityIssue()
    {
        var repo = RepoIdNormalizer.Normalize("quality-repo");
        var now = DateTime.UtcNow;
        var stale = new MemoryEntry
        {
            Id = $"memories/{repo}/insight/leaky",
            RepoId = repo,
            Type = MemoryType.Insight,
            Content = "a forgotten insight a stale L1 index still returns",
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now, ValidUntil = now },
            IsLatest = true,
        };
        var store = new LeakyIntegrityStore { LeakVia = ReadPath.ContextL1, LeakEntry = stale };
        await store.StoreAsync(stale);
        // A live entry so AnalyzeAsync does not early-return on an empty corpus.
        await store.StoreAsync(new MemoryEntry
        {
            Id = $"memories/{repo}/insight/live",
            RepoId = repo,
            Type = MemoryType.Insight,
            Content = "a live insight about the storage layer",
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now },
            IsLatest = true,
        });

        var svc = new QualityService(store, new IntegrityAuditor(new MemoryService(store), store));
        var report = await svc.AnalyzeAsync("quality-repo");

        Assert.Contains(report.Issues, i => i.CheckId == "forget-leak" && i.Severity == QualitySeverity.Critical);
    }
}

/// <summary>
/// In-memory store that resurfaces one soft-deleted entry through a single chosen read path — used to
/// prove the auditor catches a leak on each path. Recall/CrossRepo/GraphNeighbor share the recall arm.
/// </summary>
internal sealed class LeakyIntegrityStore : InMemoryEidetStore
{
    public ReadPath LeakVia { get; init; }
    public MemoryEntry? LeakEntry { get; init; }

    public override async Task<List<MemoryEntry>> FullTextSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        var results = await base.FullTextSearchAsync(repoIds, query, ct);
        if (LeakVia is ReadPath.Recall or ReadPath.CrossRepoSearch or ReadPath.GraphNeighbor
            && LeakEntry is not null && results.All(e => e.Id != LeakEntry.Id))
            results.Add(LeakEntry);
        return results;
    }

    public override Task<List<MemoryEntry>> GetTopScoredAsync(
        string repoId, MemoryType[] types, int limit, CancellationToken ct = default)
    {
        var list = LeakVia == ReadPath.ContextL1 && LeakEntry is not null ? [LeakEntry] : new List<MemoryEntry>();
        return Task.FromResult(list);
    }

    public override Task<IReadOnlyList<MemoryEntry>> FindNearDuplicatesAsync(
        string repoId, MemoryEntry entry, float minSimilarity, int max, CancellationToken ct = default)
    {
        IReadOnlyList<MemoryEntry> r = LeakVia == ReadPath.DuplicateDetection && LeakEntry is not null
            ? [LeakEntry] : [];
        return Task.FromResult(r);
    }
}
