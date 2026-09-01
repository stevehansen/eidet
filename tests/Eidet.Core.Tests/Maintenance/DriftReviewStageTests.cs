using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Drives the real <see cref="DriftReviewStage"/> through <see cref="MaintenanceOrchestrator"/>
/// (OnlyStages, like the other stage tests) against the shared <see cref="InMemoryEidetStore"/>,
/// with a seeded <see cref="InMemoryEnrichmentAdapter"/> standing in for the model.
/// </summary>
public class DriftReviewStageTests
{
    // Reason "fresh" marks entries the run actually reviewed; pre-seeded verdicts carry "old".
    private const string OkJson =
        """{"verdict":"ok","confidence":0.9,"reason":"fresh","suggested_fix":null}""";
    private const string StaleHighConfidenceJson =
        """{"verdict":"stale","confidence":0.9,"reason":"fresh","suggested_fix":"rewrite it"}""";
    private const string StaleLowConfidenceJson =
        """{"verdict":"stale","confidence":0.5,"reason":"fresh","suggested_fix":null}""";

    private static MemoryEntry MakeEntry(string id, DateTime createdAt, float confidence = 0.7f,
        bool isLatest = true, string? layerId = null, DateTime? validUntil = null,
        DateTime? forgetAfter = null, DriftReview? drift = null) => new()
    {
        Id = $"memories/repo-a/insight/{id}",
        RepoId = "repo-a",
        Type = MemoryType.Insight,
        Content = $"deployment pipeline notes {id}",
        CreatedAt = createdAt,
        Validity = new Validity { ValidFrom = createdAt, ValidUntil = validUntil },
        IsLatest = isLatest,
        LayerId = layerId,
        ForgetAfter = forgetAfter,
        Importance = 0.6f,
        Confidence = confidence,
        Drift = drift,
    };

    private static EnrichmentService WithResponse(string? response) =>
        new(new InMemoryEnrichmentAdapter().SetResponse(EnrichmentPrompt.DriftReview, response),
            modelName: "test-model");

    private static Task<MaintenanceReport> RunDriftStageAsync(
        InMemoryEidetStore store, EnrichmentService enrichment, DriftReviewConfig cfg)
    {
        var orch = new MaintenanceOrchestrator(store, new MemoryService(store), enrichment, drift: cfg);
        return orch.RunAsync(new MaintenanceRequest
        {
            RepoId = "repo-a",
            IsRepoActive = true,
            OnlyStages = new HashSet<MaintenanceStep> { MaintenanceStep.DriftReview },
        });
    }

    // ─── Gating ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DisabledConfig_ReviewsNothing_WritesNothing()
    {
        var store = new CountingStore();
        var entry = MakeEntry("e1", DateTime.UtcNow.AddDays(-30));
        await store.StoreAsync(entry);
        using var enrichment = WithResponse(OkJson);

        var report = await RunDriftStageAsync(store, enrichment, new DriftReviewConfig { Enabled = false });

        Assert.Equal(0, report.AffectedBy(DriftReviewStage.StageName));
        Assert.Null(entry.Drift);
        Assert.Equal(0, store.UpdateCount);
    }

    [Fact]
    public async Task NullEnrichment_ReviewsNothing_WritesNothing()
    {
        var store = new CountingStore();
        var entry = MakeEntry("e1", DateTime.UtcNow.AddDays(-30));
        await store.StoreAsync(entry);
        using var enrichment = EnrichmentService.CreateNull();

        var report = await RunDriftStageAsync(store, enrichment, new DriftReviewConfig());

        Assert.Equal(0, report.AffectedBy(DriftReviewStage.StageName));
        Assert.Null(entry.Drift);
        Assert.Equal(0, store.UpdateCount);
    }

    // ─── Batch + ordering ─────────────────────────────────────────────────

    [Fact]
    public async Task NightlyBatch_CapsReviewsPerRun()
    {
        var store = new InMemoryEidetStore();
        var entries = Enumerable.Range(0, 3)
            .Select(i => MakeEntry($"e{i}", DateTime.UtcNow.AddDays(-30)))
            .ToList();
        foreach (var e in entries) await store.StoreAsync(e);
        using var enrichment = WithResponse(OkJson);

        var report = await RunDriftStageAsync(store, enrichment, new DriftReviewConfig { NightlyBatch = 2 });

        Assert.Equal(2, report.AffectedBy(DriftReviewStage.StageName));
        Assert.Equal(2, entries.Count(e => e.Drift is not null));
    }

    [Fact]
    public async Task Ordering_NeverReviewedFirst_ThenOldestVerdict()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        // Inserted with the freshest cursor FIRST so a naive take-in-store-order would pick it.
        var recentVerdict = MakeEntry("recent", now.AddDays(-30),
            drift: new DriftReview { Verdict = DriftVerdictKind.Ok, ReviewedAt = now.AddDays(-3), Reason = "old" });
        var neverReviewed = MakeEntry("never", now.AddDays(-30));
        var oldestVerdict = MakeEntry("oldest", now.AddDays(-30),
            drift: new DriftReview { Verdict = DriftVerdictKind.Ok, ReviewedAt = now.AddDays(-10), Reason = "old" });
        await store.StoreAsync(recentVerdict);
        await store.StoreAsync(neverReviewed);
        await store.StoreAsync(oldestVerdict);
        using var enrichment = WithResponse(OkJson);

        // A short re-review interval keeps both seeded verdicts eligible, so this exercises the
        // ordering rather than the convergence filter below.
        var report = await RunDriftStageAsync(store, enrichment,
            new DriftReviewConfig { NightlyBatch = 2, ReviewIntervalDays = 1 });

        Assert.Equal(2, report.AffectedBy(DriftReviewStage.StageName));
        Assert.Equal("fresh", neverReviewed.Drift!.Reason); // never-reviewed wins the batch
        Assert.Equal("fresh", oldestVerdict.Drift!.Reason); // then the stalest cursor
        Assert.Equal("old", recentVerdict.Drift!.Reason);   // freshest cursor waits for a later night
    }

    // ─── Candidate filtering ──────────────────────────────────────────────

    [Fact]
    public async Task SkipsYoungNonLatestLayeredAndExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        var young = MakeEntry("young", now.AddDays(-1)); // < MinAgeDays (7)
        var notLatest = MakeEntry("superseded", now.AddDays(-30), isLatest: false);
        var layered = MakeEntry("layered", now.AddDays(-30), layerId: "layers/base");
        var expired = MakeEntry("expired", now.AddDays(-30), validUntil: now.AddDays(-1));
        var eligible = MakeEntry("eligible", now.AddDays(-30));
        foreach (var e in new[] { young, notLatest, layered, expired, eligible })
            await store.StoreAsync(e);
        using var enrichment = WithResponse(OkJson);

        var report = await RunDriftStageAsync(store, enrichment, new DriftReviewConfig());

        Assert.Equal(1, report.AffectedBy(DriftReviewStage.StageName));
        Assert.NotNull(eligible.Drift);
        Assert.Null(young.Drift);
        Assert.Null(notLatest.Drift);
        Assert.Null(layered.Drift);
        Assert.Null(expired.Drift);
    }

    // ─── Convergence ──────────────────────────────────────────────────────

    [Fact]
    public async Task SettledCorpus_CostsNothing()
    {
        // The stage's whole load profile. ReviewedAt doubles as the coverage cursor, so before the
        // re-review interval existed a corpus nobody had touched still spent NightlyBatch model
        // calls per repo per night, forever, re-reviewing the oldest verdicts in rotation.
        var now = DateTime.UtcNow;
        var store = new CountingStore();
        var entries = Enumerable.Range(0, 5)
            .Select(i => MakeEntry($"e{i}", now.AddDays(-300), drift: new DriftReview
            {
                Verdict = DriftVerdictKind.Ok, ReviewedAt = now.AddDays(-i - 1), Reason = "old",
            }))
            .ToList();
        foreach (var e in entries) await store.StoreAsync(e);
        using var enrichment = WithResponse(OkJson);

        var report = await RunDriftStageAsync(store, enrichment, new DriftReviewConfig());

        Assert.Equal(0, report.AffectedBy(DriftReviewStage.StageName));
        Assert.All(entries, e => Assert.Equal("old", e.Drift!.Reason));
        Assert.Equal(0, store.UpdateCount);
    }

    [Fact]
    public async Task VerdictOlderThanTheInterval_IsOfferedToTheModelAgain()
    {
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        var aged = MakeEntry("aged", now.AddDays(-300), drift: new DriftReview
        {
            Verdict = DriftVerdictKind.Ok, ReviewedAt = now.AddDays(-91), Reason = "old",
        });
        var recent = MakeEntry("recent", now.AddDays(-300), drift: new DriftReview
        {
            Verdict = DriftVerdictKind.Ok, ReviewedAt = now.AddDays(-89), Reason = "old",
        });
        await store.StoreAsync(aged);
        await store.StoreAsync(recent);
        using var enrichment = WithResponse(OkJson);

        var report = await RunDriftStageAsync(store, enrichment, new DriftReviewConfig());

        Assert.Equal(1, report.AffectedBy(DriftReviewStage.StageName));
        Assert.Equal("fresh", aged.Drift!.Reason);
        Assert.Equal("old", recent.Drift!.Reason);
    }

    [Fact]
    public async Task ReviewIntervalZero_RestoresNightlyReReview()
    {
        // The escape hatch for anyone who wants the old always-on sweep back.
        var now = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        var entry = MakeEntry("e1", now.AddDays(-30), drift: new DriftReview
        {
            Verdict = DriftVerdictKind.Ok, ReviewedAt = now, Reason = "old",
        });
        await store.StoreAsync(entry);
        using var enrichment = WithResponse(OkJson);

        var report = await RunDriftStageAsync(store, enrichment, new DriftReviewConfig { ReviewIntervalDays = 0 });

        Assert.Equal(1, report.AffectedBy(DriftReviewStage.StageName));
        Assert.Equal("fresh", entry.Drift!.Reason);
    }

    [Fact]
    public void DefaultsToQuarterlyReReview()
    {
        Assert.Equal(90, new DriftReviewConfig().ReviewIntervalDays);
    }

    // ─── Verdict handling per autonomy ────────────────────────────────────

    [Fact]
    public async Task OkVerdict_WritesDrift_LeavesConfidenceAlone()
    {
        var runStart = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        var entry = MakeEntry("e1", runStart.AddDays(-30));
        await store.StoreAsync(entry);
        using var enrichment = WithResponse(OkJson);

        var report = await RunDriftStageAsync(store, enrichment, new DriftReviewConfig { Autonomy = DriftAutonomy.Decay });

        Assert.Equal(1, report.AffectedBy(DriftReviewStage.StageName));
        Assert.NotNull(entry.Drift);
        Assert.Equal(DriftVerdictKind.Ok, entry.Drift.Verdict);
        Assert.Equal("test-model", entry.Drift.Model);
        Assert.InRange(entry.Drift.ReviewedAt, runStart, DateTime.UtcNow); // cursor advanced
        Assert.Equal(0.7f, entry.Confidence);
        Assert.Equal(0.6f, entry.Importance);
        Assert.Null(entry.ForgetAfter);
    }

    [Fact]
    public async Task DecayAutonomy_ConfidentNonOkVerdict_DecaysConfidence()
    {
        var store = new InMemoryEidetStore();
        var entry = MakeEntry("e1", DateTime.UtcNow.AddDays(-30), confidence: 0.7f);
        await store.StoreAsync(entry);
        using var enrichment = WithResponse(StaleHighConfidenceJson);

        await RunDriftStageAsync(store, enrichment, new DriftReviewConfig { Autonomy = DriftAutonomy.Decay });

        Assert.Equal(DriftVerdictKind.Stale, entry.Drift!.Verdict);
        Assert.Equal(0.55f, entry.Confidence, 0.0001f);
        Assert.Equal(0.6f, entry.Importance);
        Assert.Null(entry.ForgetAfter);
    }

    [Fact]
    public async Task DecayAutonomy_NeverDropsConfidenceBelowFloor()
    {
        var store = new InMemoryEidetStore();
        var entry = MakeEntry("e1", DateTime.UtcNow.AddDays(-30), confidence: 0.25f);
        await store.StoreAsync(entry);
        using var enrichment = WithResponse(StaleHighConfidenceJson);

        await RunDriftStageAsync(store, enrichment, new DriftReviewConfig { Autonomy = DriftAutonomy.Decay });

        Assert.Equal(0.2f, entry.Confidence, 0.0001f);
    }

    [Fact]
    public async Task DecayAutonomy_NeverRaisesConfidenceAlreadyBelowFloor()
    {
        var store = new InMemoryEidetStore();
        // Fizzle feedback can push confidence below the 0.2 decay floor; a negative
        // verdict must never boost such an entry back up to the floor.
        var entry = MakeEntry("e1", DateTime.UtcNow.AddDays(-30), confidence: 0.05f);
        await store.StoreAsync(entry);
        using var enrichment = WithResponse(StaleHighConfidenceJson);

        await RunDriftStageAsync(store, enrichment, new DriftReviewConfig { Autonomy = DriftAutonomy.Decay });

        Assert.Equal(DriftVerdictKind.Stale, entry.Drift!.Verdict);
        Assert.Equal(0.05f, entry.Confidence, 0.0001f);
    }

    [Fact]
    public async Task NonOkVerdictBelowMinModelConfidence_RecordsVerdictWithoutDecay()
    {
        var store = new InMemoryEidetStore();
        var entry = MakeEntry("e1", DateTime.UtcNow.AddDays(-30), confidence: 0.7f);
        await store.StoreAsync(entry);
        using var enrichment = WithResponse(StaleLowConfidenceJson); // 0.5 < MinModelConfidence 0.7

        await RunDriftStageAsync(store, enrichment, new DriftReviewConfig { Autonomy = DriftAutonomy.Decay });

        Assert.Equal(DriftVerdictKind.Stale, entry.Drift!.Verdict);
        Assert.Equal(0.5f, entry.Drift.ModelConfidence);
        Assert.Equal(0.7f, entry.Confidence);
        Assert.Equal(0.6f, entry.Importance);
        Assert.Null(entry.ForgetAfter);
    }

    [Fact]
    public async Task FlagOnlyAutonomy_RecordsVerdictWithoutDecayOrExpiry()
    {
        var store = new InMemoryEidetStore();
        var entry = MakeEntry("e1", DateTime.UtcNow.AddDays(-30), confidence: 0.7f);
        await store.StoreAsync(entry);
        using var enrichment = WithResponse(StaleHighConfidenceJson);

        await RunDriftStageAsync(store, enrichment, new DriftReviewConfig { Autonomy = DriftAutonomy.FlagOnly });

        Assert.Equal(DriftVerdictKind.Stale, entry.Drift!.Verdict);
        Assert.Equal(0.7f, entry.Confidence);
        Assert.Equal(0.6f, entry.Importance);
        Assert.Null(entry.ForgetAfter);
    }

    [Fact]
    public async Task ExpireAutonomy_SetsForgetAfter_OnlyWhenPreviouslyNull()
    {
        var runStart = DateTime.UtcNow;
        var store = new InMemoryEidetStore();
        var existingTtl = runStart.AddDays(100);
        var noTtl = MakeEntry("no-ttl", runStart.AddDays(-30));
        var withTtl = MakeEntry("with-ttl", runStart.AddDays(-30), forgetAfter: existingTtl);
        await store.StoreAsync(noTtl);
        await store.StoreAsync(withTtl);
        using var enrichment = WithResponse(StaleHighConfidenceJson);

        await RunDriftStageAsync(store, enrichment, new DriftReviewConfig { Autonomy = DriftAutonomy.Expire });

        Assert.NotNull(noTtl.ForgetAfter);
        Assert.InRange(noTtl.ForgetAfter.Value, runStart.AddDays(14), DateTime.UtcNow.AddDays(14));
        Assert.Equal(existingTtl, withTtl.ForgetAfter); // pre-existing TTL preserved
        // Importance never changes in any autonomy path; confidence still decays under Expire.
        Assert.Equal(0.6f, noTtl.Importance);
        Assert.Equal(0.6f, withTtl.Importance);
        Assert.Equal(0.55f, noTtl.Confidence, 0.0001f);
    }

    // ─── Parser failure ───────────────────────────────────────────────────

    [Fact]
    public async Task UnparseableResponse_SkipsEntryWithoutWriteOrCursorAdvance()
    {
        var store = new CountingStore();
        var entry = MakeEntry("e1", DateTime.UtcNow.AddDays(-30));
        await store.StoreAsync(entry);
        using var enrichment = WithResponse("no json here at all");

        var report = await RunDriftStageAsync(store, enrichment, new DriftReviewConfig());

        Assert.Equal(0, report.AffectedBy(DriftReviewStage.StageName));
        Assert.Null(entry.Drift); // cursor untouched — retried on a future night
        Assert.Equal(0, store.UpdateCount);
    }

    // ─── Orchestrator seam ────────────────────────────────────────────────

    [Fact]
    public async Task OnlyStagesDriftReview_RunsJustTheDriftStage()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(MakeEntry("e1", DateTime.UtcNow.AddDays(-30)));
        using var enrichment = WithResponse(OkJson);

        // Default stage list — OnlyStages must filter the full pipeline down to DriftReview.
        var report = await RunDriftStageAsync(store, enrichment, new DriftReviewConfig());

        var outcome = Assert.Single(report.Stages);
        Assert.Equal(DriftReviewStage.StageName, outcome.Name);
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.Affected);
    }

    /// <summary>Counts UpdateAsync calls — the write path BulkMutationCtx.WriteAsync routes through.</summary>
    private sealed class CountingStore : InMemoryEidetStore
    {
        public int UpdateCount { get; private set; }

        public override Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default)
        {
            UpdateCount++;
            return base.UpdateAsync(entry, ct);
        }
    }
}
