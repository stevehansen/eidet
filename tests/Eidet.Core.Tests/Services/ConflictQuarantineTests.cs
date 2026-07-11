using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Text;

namespace Eidet.Core.Tests.Services;

/// <summary>
/// Write-time conflict check + soft-quarantine lifecycle (#37). Covers: a high-trust contradiction is
/// quarantined (still stored); low-trust incumbent and explicit supersession are exempt; a quarantined
/// memory is downranked but still recallable; an echo clears it; a Released edit reverses it; and a
/// repeat contradiction fast-paths to Rejected via the poison log.
/// </summary>
public class ConflictQuarantineTests
{
    private static readonly string Repo = RepoIdNormalizer.Normalize("conflict-repo");
    private const string Claim = "RavenDB embedded mode is the recommended zero-setup default storage";

    private static MemoryEntry Incumbent(Valence valence, MemoryProvenance provenance, string content = Claim) => new()
    {
        Id = $"memories/{Repo}/insight/{Guid.NewGuid():N}",
        RepoId = Repo,
        Type = MemoryType.Insight,
        Valence = valence,
        Provenance = provenance,
        Content = content,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
        Importance = 0.7f,
    };

    // ─── The gate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefutingContradictionOfHighTrustIncumbent_IsQuarantinedButStored()
    {
        var store = new NearDupStore();
        var incumbent = Incumbent(Valence.Affirming, MemoryProvenance.AgentInferred); // trust 1.0
        await store.StoreAsync(incumbent);
        var svc = new MemoryService(store);

        var r = await svc.StoreAsync(new StoreOptions(Repo, Claim, MemoryType.Insight) { Valence = Valence.Refuting });

        Assert.True(r.Success);                 // still stored (append-only)
        Assert.True(r.Quarantined);
        Assert.NotNull(r.Conflict);
        Assert.Equal(incumbent.Id, r.Conflict!.Value.ContradictedId);

        var stored = await store.GetAsync(r.Id!);
        Assert.NotNull(stored!.Quarantine);
        Assert.False(stored.Quarantine!.Released);
    }

    [Fact]
    public async Task ContradictionOfLowTrustIncumbent_IsStoredNormally()
    {
        var store = new NearDupStore();
        await store.StoreAsync(Incumbent(Valence.Affirming, MemoryProvenance.Pack)); // trust floor 0.5 < 0.9
        var svc = new MemoryService(store);

        var r = await svc.StoreAsync(new StoreOptions(Repo, Claim, MemoryType.Insight) { Valence = Valence.Refuting });

        Assert.True(r.Success);
        Assert.False(r.Quarantined);
        Assert.Null((await store.GetAsync(r.Id!))!.Quarantine);
    }

    [Fact]
    public async Task Supersession_IsExemptFromConflictCheck()
    {
        var store = new NearDupStore();
        var incumbent = Incumbent(Valence.Affirming, MemoryProvenance.AgentInferred); // trust 1.0
        await store.StoreAsync(incumbent);
        var svc = new MemoryService(store);

        // An explicit correction contradicts the incumbent by design — it must never be quarantined.
        var r = await svc.StoreAsync(new StoreOptions(Repo, Claim, MemoryType.Insight)
        {
            Valence = Valence.Refuting,
            Supersedes = incumbent.Id,
        });

        Assert.True(r.Success);
        Assert.False(r.Quarantined);
        Assert.Null((await store.GetAsync(r.Id!))!.Quarantine);
    }

    [Fact]
    public async Task CrossTypeContradiction_IsCaughtViaTheExactDuplicate()
    {
        // The design's own example: an Affirming Insight incumbent vs a Refuting Heuristic. The same-type
        // near-dup query misses it, but the type-agnostic exact-duplicate (folded into the neighbor pool)
        // catches it.
        var store = new NearDupStore();
        var incumbent = Incumbent(Valence.Affirming, MemoryProvenance.AgentInferred); // Insight, trust 1.0
        await store.StoreAsync(incumbent);
        var svc = new MemoryService(store);

        var r = await svc.StoreAsync(new StoreOptions(Repo, Claim, MemoryType.Heuristic) { Valence = Valence.Refuting });

        Assert.True(r.Quarantined);
        Assert.Equal(incumbent.Id, r.Conflict!.Value.ContradictedId);
    }

    [Fact]
    public async Task NeutralStore_SkipsConflictCheck_NoQuarantine()
    {
        var store = new NearDupStore();
        await store.StoreAsync(Incumbent(Valence.Affirming, MemoryProvenance.AgentInferred));
        var svc = new MemoryService(store);

        var r = await svc.StoreAsync(new StoreOptions(Repo, Claim + " with WAL journaling", MemoryType.Insight));

        Assert.True(r.Success);
        Assert.False(r.Quarantined);
    }

    // ─── Lifecycle: downrank, echo-clear, release ──────────────────────────────

    private static MemoryEntry Recallable(string idSuffix, string content, bool quarantined)
    {
        var e = Incumbent(Valence.Neutral, MemoryProvenance.AgentInferred, content);
        e.Id = $"memories/{Repo}/insight/{idSuffix}";
        if (quarantined)
            e.Quarantine = new QuarantineInfo { ContradictedId = "x", Released = false, QuarantinedAt = DateTime.UtcNow };
        return e;
    }

    [Fact]
    public async Task QuarantinedMemory_IsDownrankedButStillRecallable()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Recallable("normal", "kubernetes deployment uses argo rollouts", quarantined: false));
        await store.StoreAsync(Recallable("quarantined", "kubernetes deployment uses argo pipelines", quarantined: true));
        var svc = new MemoryService(store);

        var results = await svc.RecallAsync(Repo, "kubernetes deployment argo");

        var normal = results.Single(r => r.Id.EndsWith("/normal"));
        var quarantined = results.Single(r => r.Id.EndsWith("/quarantined")); // still recallable
        Assert.True(quarantined.Score < normal.Score, $"quarantined {quarantined.Score} !< normal {normal.Score}");
    }

    [Fact]
    public async Task Echo_ClearsQuarantine()
    {
        var store = new InMemoryEidetStore();
        var q = Recallable("quarantined", "redis caching layer with 5 minute ttl", quarantined: true);
        await store.StoreAsync(q);
        var svc = new MemoryService(store);

        Assert.True(await svc.FeedbackAsync(q.Id, wasUsed: true));

        Assert.Null((await store.GetAsync(q.Id))!.Quarantine);
    }

    [Fact]
    public async Task ReleaseQuarantine_Edit_ReversesTheDeBoost()
    {
        var store = new InMemoryEidetStore();
        var released = Recallable("released", "postgres connection pool sized to sixteen", quarantined: true);
        var control = Recallable("control", "postgres connection pool sized to sixteen", quarantined: true);
        await store.StoreAsync(released);
        await store.StoreAsync(control);
        var svc = new MemoryService(store);

        Assert.Equal(EditOutcome.Updated, await svc.EditAsync(released.Id, new EditOptions { ReleaseQuarantine = true }));

        var verdict = (await store.GetAsync(released.Id))!.Quarantine;
        Assert.NotNull(verdict);            // record kept for the audit trail
        Assert.True(verdict!.Released);     // but no longer de-boosts

        var results = await svc.RecallAsync(Repo, "postgres connection pool sixteen");
        var releasedResult = results.Single(r => r.Id.EndsWith("/released"));
        var controlResult = results.Single(r => r.Id.EndsWith("/control")); // still quarantined
        Assert.True(releasedResult.Score > controlResult.Score);
    }

    // ─── Poison log fast-path ──────────────────────────────────────────────────

    [Fact]
    public async Task RepeatContradiction_FastPathsToRejected_ViaPoisonLog()
    {
        var store = new NearDupStore();
        await store.StoreAsync(Incumbent(Valence.Affirming, MemoryProvenance.AgentInferred));
        var poison = new InMemoryPoisonLog();
        var svc = new MemoryService(store, poison: poison);

        var first = await svc.StoreAsync(new StoreOptions(Repo, Claim, MemoryType.Insight) { Valence = Valence.Refuting });
        Assert.True(first.Quarantined);     // records the poison pattern

        var second = await svc.StoreAsync(new StoreOptions(Repo, Claim, MemoryType.Insight) { Valence = Valence.Refuting });
        Assert.False(second.Success);
        Assert.Contains("poison", second.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PoisonFastPath_ExemptsExplicitSupersession()
    {
        var store = new NearDupStore();
        var incumbent = Incumbent(Valence.Affirming, MemoryProvenance.AgentInferred);
        await store.StoreAsync(incumbent);
        var poison = new InMemoryPoisonLog();
        // Seed the poison log directly so the exemption is isolated from the quarantine machinery.
        await poison.RecordAsync(Repo, new ConflictFinding(incumbent.Id, Valence.Refuting, Valence.Affirming, 1.0f, 1.0), Claim);
        var svc = new MemoryService(store, poison: poison);

        // A non-correction store of the poisoned content is rejected...
        Assert.False((await svc.StoreAsync(new StoreOptions(Repo, Claim, MemoryType.Insight) { Valence = Valence.Refuting })).Success);

        // ...but the same content as an explicit correction must NOT be poison-rejected.
        var correction = await svc.StoreAsync(new StoreOptions(Repo, Claim, MemoryType.Insight)
        {
            Valence = Valence.Refuting,
            Supersedes = incumbent.Id,
        });
        Assert.True(correction.Success);
        Assert.False(correction.Quarantined);
    }
}

/// <summary>Store fake with a realistic word-overlap near-duplicate search over the seeded corpus.</summary>
internal sealed class NearDupStore : InMemoryEidetStore
{
    // Same-type near-duplicates (mirrors the RavenDB adapter's Type filter).
    public override async Task<IReadOnlyList<MemoryEntry>> FindNearDuplicatesAsync(
        string repoId, MemoryEntry entry, float minSimilarity, int max, CancellationToken ct = default)
    {
        var candidates = await BrowseAsync(repoId, 0, 500, entry.Type, ct);
        return candidates
            .Where(e => e.Id != entry.Id && e.IsLatest && WordSimilarity.Compute(e.Content, entry.Content) >= minSimilarity)
            .Take(max)
            .ToList();
    }

    // Type-AGNOSTIC exact-duplicate (mirrors the RavenDB adapter's dup-gate, which has no Type clause).
    public override async Task<MemoryEntry?> FindDuplicateAsync(
        string repoId, string content, float threshold, CancellationToken ct = default)
    {
        var candidates = await BrowseAsync(repoId, 0, 500, ct: ct);
        return candidates.FirstOrDefault(e =>
            e.IsLatest && WordSimilarity.Compute(e.Content, content) >= threshold);
    }
}

/// <summary>In-memory poison log keyed by the shared content fingerprint.</summary>
internal sealed class InMemoryPoisonLog : IPoisonLog
{
    private readonly Dictionary<string, PoisonPattern> _byId = new(StringComparer.OrdinalIgnoreCase);

    public Task<PoisonPattern?> MatchAsync(string repoId, string content, CancellationToken ct = default) =>
        Task.FromResult(_byId.GetValueOrDefault(Key(repoId, content)));

    public Task RecordAsync(string repoId, ConflictFinding c, string content, CancellationToken ct = default)
    {
        var key = Key(repoId, content);
        if (_byId.TryGetValue(key, out var p))
        {
            p.Attempts++;
            p.LastSeenAt = DateTime.UtcNow;
        }
        else
        {
            _byId[key] = new PoisonPattern
            {
                Id = key,
                RepoId = repoId,
                Fingerprint = IPoisonLog.Fingerprint(content),
                ContradictedId = c.ContradictedId,
                Stance = c.Stance,
                ContradictedStance = c.ContradictedStance,
                ContradictedTrust = c.ContradictedTrust,
                SampleContent = content,
                FirstSeenAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
            };
        }
        return Task.CompletedTask;
    }

    private static string Key(string repoId, string content) => $"{repoId}/{IPoisonLog.Fingerprint(content)}";
}
