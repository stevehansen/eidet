using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Maintenance.Stages;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Drives the real <see cref="OllamaEnrichmentStage"/> through <see cref="MaintenanceOrchestrator"/>
/// (OnlyStages, like the other stage tests) against the shared <see cref="InMemoryEidetStore"/>.
/// The stage is the retry net for the EnrichmentWorker: it must select exactly the docs still
/// awaiting enrichment, not a scored slice.
/// </summary>
public class OllamaEnrichmentStageTests
{
    private static MemoryEntry MakeEntry(string id, string? summary = null,
        string content = "deployment pipeline notes", DateTime? createdAt = null,
        bool isLatest = true, DateTime? validUntil = null) => new()
    {
        Id = $"memories/repo-a/insight/{id}",
        RepoId = "repo-a",
        Type = MemoryType.Insight,
        Content = content,
        Summary = summary,
        CreatedAt = createdAt ?? DateTime.UtcNow.AddDays(-1),
        Validity = new Validity { ValidFrom = DateTime.UtcNow.AddDays(-1), ValidUntil = validUntil },
        IsLatest = isLatest,
    };

    private static EnrichmentService FullResponses() =>
        new(new InMemoryEnrichmentAdapter()
            .SetResponse(EnrichmentPrompt.Summary, "a summary")
            .SetResponse(EnrichmentPrompt.OneLiner, "a one-liner")
            .SetResponse(EnrichmentPrompt.ForesightHint, "a hint")
            .SetResponse(EnrichmentPrompt.Entities, "EntityA\nEntityB"));

    private static Task<MaintenanceReport> RunStageAsync(InMemoryEidetStore store, EnrichmentService enrichment)
    {
        var orch = new MaintenanceOrchestrator(store, new MemoryService(store), enrichment);
        return orch.RunAsync(new MaintenanceRequest
        {
            RepoId = "repo-a",
            IsRepoActive = true,
            OnlyStages = new HashSet<MaintenanceStep> { MaintenanceStep.OllamaEnrichment },
        });
    }

    [Fact]
    public async Task EnrichesUnenrichedDocs_SkipsAlreadySummarized()
    {
        var store = new InMemoryEidetStore();
        var pending1 = MakeEntry("p1");
        var pending2 = MakeEntry("p2");
        var done = MakeEntry("d1", summary: "already summarized");
        await store.StoreAsync(pending1);
        await store.StoreAsync(pending2);
        await store.StoreAsync(done);
        using var enrichment = FullResponses();

        var report = await RunStageAsync(store, enrichment);

        Assert.Equal(2, report.AffectedBy(OllamaEnrichmentStage.StageName));
        Assert.Equal("a summary", pending1.Summary);
        Assert.Equal("a summary", pending2.Summary);
        Assert.Equal("already summarized", done.Summary);
    }

    [Fact]
    public async Task SkipsSupersededAndForgottenDocs()
    {
        var store = new InMemoryEidetStore();
        var superseded = MakeEntry("old", isLatest: false);
        var forgotten = MakeEntry("gone", validUntil: DateTime.UtcNow.AddHours(-1));
        await store.StoreAsync(superseded);
        await store.StoreAsync(forgotten);
        using var enrichment = FullResponses();

        var report = await RunStageAsync(store, enrichment);

        Assert.Equal(0, report.AffectedBy(OllamaEnrichmentStage.StageName));
        Assert.Null(superseded.Summary);
        Assert.Null(forgotten.Summary);
    }

    [Fact]
    public async Task SkipsRedactionTombstones()
    {
        var store = new InMemoryEidetStore();
        // A pre-fix tombstone: Summary still null but content scrubbed. The stage selects it
        // (no content filter in the query) but enrichment must refuse to re-describe it.
        var tombstone = MakeEntry("t1", content: $"{MemoryEntry.RedactedPrefix} GDPR @ 2026-01-01]");
        await store.StoreAsync(tombstone);
        using var enrichment = FullResponses();

        var report = await RunStageAsync(store, enrichment);

        Assert.Equal(0, report.AffectedBy(OllamaEnrichmentStage.StageName));
        Assert.Null(tombstone.Summary);
    }

    [Fact]
    public async Task UnavailableEnrichment_DoesNothing()
    {
        var store = new InMemoryEidetStore();
        var pending = MakeEntry("p1");
        await store.StoreAsync(pending);
        using var enrichment = new EnrichmentService(new InMemoryEnrichmentAdapter { IsAvailable = false });

        var report = await RunStageAsync(store, enrichment);

        Assert.Equal(0, report.AffectedBy(OllamaEnrichmentStage.StageName));
        Assert.Null(pending.Summary);
    }

    [Fact]
    public async Task BatchLimit_CapsAttemptsPerRun_OldestFirst()
    {
        var store = new InMemoryEidetStore();
        var entries = Enumerable.Range(0, OllamaEnrichmentStage.BatchLimit + 5)
            .Select(i => MakeEntry($"e{i}", createdAt: DateTime.UtcNow.AddDays(-100 + i)))
            .ToList();
        foreach (var e in entries) await store.StoreAsync(e);
        using var enrichment = FullResponses();

        var report = await RunStageAsync(store, enrichment);

        Assert.Equal(OllamaEnrichmentStage.BatchLimit, report.AffectedBy(OllamaEnrichmentStage.StageName));
        // Oldest first: the overflow (newest) docs wait for the next run.
        Assert.All(entries.Take(OllamaEnrichmentStage.BatchLimit), e => Assert.Equal("a summary", e.Summary));
        Assert.All(entries.Skip(OllamaEnrichmentStage.BatchLimit), e => Assert.Null(e.Summary));
    }

    [Fact]
    public async Task FailedEnrichment_LeavesDocForNextRun()
    {
        var store = new InMemoryEidetStore();
        var pending = MakeEntry("p1");
        await store.StoreAsync(pending);
        // Model returns nothing for every prompt: EnrichMemoryAsync reports no change.
        using var enrichment = new EnrichmentService(new InMemoryEnrichmentAdapter());

        var report = await RunStageAsync(store, enrichment);

        Assert.Equal(0, report.AffectedBy(OllamaEnrichmentStage.StageName));
        // Still unenriched — the next sweep selects it again (unlike the worker's ack-and-forget).
        var next = await store.GetUnenrichedAsync("repo-a", 10);
        Assert.Single(next);
    }
}
