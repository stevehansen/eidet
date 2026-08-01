using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Eidet.Core.Services;
// Reuses the suite's existing hand-rolled TimeProvider double (no
// Microsoft.Extensions.TimeProvider.Testing dependency); aliased so the Canon namespace's other
// test doubles stay out of scope here.
using FakeClock = Eidet.Core.Tests.Canon.FakeTimeProvider;

namespace Eidet.Core.Tests.Services;

/// <summary>
/// Curation-safety hardening (#65): content_sha256 optimistic concurrency on the edit path, and the
/// redact verb that scrubs content while preserving the audit node.
/// </summary>
public class CurationSafetyTests
{
    private const string Repo = "curation-repo";

    // ─── content_sha256 optimistic concurrency ─────────────────────────

    [Fact]
    public async Task Edit_StaleSha_IsPreconditionFailed_AndDoesNotSupersede()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var stored = await svc.StoreAsync(Repo, "the original content about the parser module", MemoryType.Insight);

        var outcome = await svc.EditAsync(stored.Id!, new EditOptions
        {
            Content = "a concurrent rewrite of the parser module content",
            ExpectedContentSha256 = ContentHash.Of("SOMETHING ELSE — stale"),
        });

        Assert.Equal(EditOutcome.PreconditionFailed, outcome);
        // No supersede: the original is still the latest, valid, unchanged.
        var current = await store.GetAsync(stored.Id!);
        Assert.True(current!.IsLatest);
        Assert.Null(current.Validity.ValidUntil);
        Assert.Equal("the original content about the parser module", current.Content);
    }

    [Fact]
    public async Task Edit_MatchingSha_Supersedes()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        const string original = "the original content about the parser module";
        var stored = await svc.StoreAsync(Repo, original, MemoryType.Insight);

        var outcome = await svc.EditAsync(stored.Id!, new EditOptions
        {
            Content = "a rewrite of the parser module content that supersedes",
            ExpectedContentSha256 = ContentHash.Of(original),
        });

        Assert.Equal(EditOutcome.Superseded, outcome);
        Assert.False((await store.GetAsync(stored.Id!))!.IsLatest); // original superseded
    }

    [Fact]
    public async Task Edit_NoSha_IsBackwardCompatible()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var stored = await svc.StoreAsync(Repo, "the original content about the parser module", MemoryType.Insight);

        // Metadata edit, no precondition → Updated (in place); content edit, no precondition → Superseded.
        Assert.Equal(EditOutcome.Updated, await svc.EditAsync(stored.Id!, new EditOptions { Importance = 0.9f }));
        Assert.Equal(EditOutcome.Superseded, await svc.EditAsync(stored.Id!, new EditOptions { Content = "blind last-write-wins rewrite of the content" }));
    }

    // ─── redact ────────────────────────────────────────────────────────

    [Fact]
    public async Task Redact_ScrubsContentAndSearchFields_KeepsAuditNode()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var entry = new MemoryEntry
        {
            Id = $"memories/{Repo}/insight/target",
            RepoId = Repo,
            Type = MemoryType.Insight,
            Content = "secret token ghp_verysecretvalue lives in the deploy notes",
            Summary = "deploy notes summary",
            OneLiner = "deploy notes",
            ForesightHint = "watch the token",
            Entities = ["ghp_verysecretvalue"],
            EchoCount = 4,
            FizzleCount = 1,
            AccessCount = 9,
            IsLatest = true,
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            Links = [new MemoryLink { TargetRepoId = Repo, TargetMemoryId = "memories/x", Relation = "related" }],
        };
        await store.StoreAsync(entry);

        Assert.True(await svc.RedactAsync(entry.Id, "GDPR erasure request 42"));

        var after = await store.GetAsync(entry.Id);
        // Scrubbed payload. Summary becomes "" (not null): null means "awaiting enrichment"
        // to the worker subscription, the nightly sweep, and the unenriched stats.
        Assert.StartsWith("[redacted:", after!.Content);
        Assert.Contains("GDPR erasure request 42", after.Content);
        Assert.Equal("", after.Summary);
        Assert.Null(after.OneLiner);
        Assert.Null(after.ForesightHint);
        Assert.Empty(after.Entities);
        // And therefore the tombstone never re-enters the enrichment backlog.
        Assert.DoesNotContain(await store.GetUnenrichedAsync(Repo, 10), e => e.Id == entry.Id);
        // Preserved audit structure.
        Assert.Equal(entry.Id, after.Id);
        Assert.True(after.IsLatest);
        Assert.Null(after.Validity.ValidUntil);
        Assert.Equal(4, after.EchoCount);
        Assert.Equal(1, after.FizzleCount);
        Assert.Equal(9, after.AccessCount);
        Assert.Single(after.Links);
    }

    [Fact]
    public async Task Redact_IsIdempotent()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var stored = await svc.StoreAsync(Repo, "content to be erased for privacy reasons here", MemoryType.Insight);

        Assert.True(await svc.RedactAsync(stored.Id!, "reason one"));
        var afterFirst = (await store.GetAsync(stored.Id!))!.Content;
        Assert.True(await svc.RedactAsync(stored.Id!, "reason two")); // no-op, keeps the first tombstone
        Assert.Equal(afterFirst, (await store.GetAsync(stored.Id!))!.Content);
    }

    [Fact]
    public async Task Redact_WorksOnSupersededMidChainNode()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var v1 = await svc.StoreAsync(Repo, "version one content about the token handling", MemoryType.Insight);
        await svc.EditAsync(v1.Id!, new EditOptions { Content = "version two content about the token handling" });

        // v1 is now a superseded mid-chain node.
        var superseded = await store.GetAsync(v1.Id!);
        Assert.False(superseded!.IsLatest);

        Assert.True(await svc.RedactAsync(v1.Id!, "scrub the superseded secret"));

        var after = await store.GetAsync(v1.Id!);
        Assert.StartsWith("[redacted:", after!.Content);
        Assert.False(after.IsLatest);                  // still superseded
        Assert.NotNull(after.Validity.ValidUntil);     // validity interval preserved
    }

    // ─── redact: the tombstone timestamp is an injected clock read (#80) ──

    /// <summary>
    /// The amendment timestamp is part of the content shape <see cref="MemoryCommitment"/> reads, so it has
    /// to be assertable rather than whatever <c>DateTime.UtcNow</c> happened to be. This is the only clock
    /// read in MemoryService that is injectable, for exactly that reason.
    /// </summary>
    [Fact]
    public async Task Redact_StampsTheInjectedClock_AndReadsAsAmended()
    {
        var when = new DateTimeOffset(2026, 6, 1, 8, 15, 30, TimeSpan.Zero);
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store, clock: new FakeClock(when));
        var stored = await svc.StoreAsync(Repo, "a note carrying personal data that must be erased", MemoryType.Insight);

        Assert.True(await svc.RedactAsync(stored.Id!, "GDPR erasure request 42"));

        var after = (await store.GetAsync(stored.Id!))!;
        Assert.Equal(
            MemoryCommitment.Render("redacted", "GDPR erasure request 42", when.UtcDateTime),
            after.Content);
        Assert.Contains("2026-06-01T08:15:30.0000000Z", after.Content);
        // And the rewrite classifies as a sanctioned amendment, not as tampering — so recall does not
        // de-boost a memory for honoring an erasure request.
        Assert.Equal(CommitmentStatus.Amended, MemoryCommitment.Check(after));
        Assert.Equal(1.0, MemoryTrust.Factor(after));
    }

    [Fact]
    public async Task Redact_RedactedContent_NoLongerSurfacesInRecall()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var stored = await svc.StoreAsync(Repo, "distinctivezebraphrase in the deployment notes", MemoryType.Insight);

        Assert.Contains(await svc.RecallAsync(Repo, "distinctivezebraphrase"), r => r.Id == stored.Id);

        await svc.RedactAsync(stored.Id!, "scrub");

        Assert.DoesNotContain(await svc.RecallAsync(Repo, "distinctivezebraphrase"), r => r.Id == stored.Id);
    }
}
