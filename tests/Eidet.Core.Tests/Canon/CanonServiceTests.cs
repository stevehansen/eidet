using Eidet.Core.Canon;
using Eidet.Core.Domain;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Canon;

/// <summary>
/// Boundary tests for <see cref="CanonService"/> driven entirely through fakes (zero RavenDB): the
/// in-memory draft store, a recording/gated mint port, a scripted draft source, and a deterministic clock.
/// Covers the review loop, the double-mint claim protocol, the regeneration damper matrix, and degraded
/// citation hydration — the contract named in CanonSpec §Testing Strategy (issue #75).
/// </summary>
public class CanonServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    // ─── 1. Full loop: regenerate → approve → re-approve (supersede) ─────

    [Fact]
    public async Task FullLoop_Regenerate_Approve_Reapprove_MintsLineage_ThenSupersedes()
    {
        var clock = new FakeTimeProvider(T0);
        var drafts = new InMemoryCanonDraftStore();
        var mint = new RecordingCanonMintPort();
        var source = new ScriptedCanonDraftSource
        {
            Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: the service issues short-lived RS256 JWT session tokens", "m1", "m2")],
        };
        var svc = new CanonService(drafts, mint, [source], new InMemoryEidetStore(), clock);

        // First regenerate creates one draft; a second identical run is a no-op (idempotent + damped).
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        Assert.Equal(0, await svc.RegenerateDraftsAsync("repo-a"));
        Assert.Equal(1, drafts.Count);

        var draft = Assert.Single(await svc.ListPendingAsync("repo-a"));
        Assert.False(draft.ReplacesExisting);

        // Approve → mint. The fake mint port receives the DRAFT; the boundary facts it carries are the
        // member snapshot (the adapter turns it into DerivedFrom) and slug/kind (→ canon:term:<slug>).
        // The concrete DerivedFrom / Source="canon-review" / tag ON THE MINTED MEMORY are asserted in
        // CanonGateIntegrationTests, which mints through the real MemoryService gate.
        var a1 = await svc.ApproveAsync(draft.Id);
        Assert.True(a1.Success);
        Assert.Equal(1, mint.CallCount);
        Assert.Equal(mint.LastMint.MemoryId, a1.MintedMemoryId);
        Assert.Equal(new[] { "m1", "m2" }, mint.LastMint.MemberIds);
        Assert.Equal("auth", mint.LastMint.Slug);
        Assert.Equal(CanonKind.Term, mint.LastMint.Kind);
        Assert.Null(mint.LastMint.SupersedesCanonId);   // first mint supersedes nothing

        // Drift the same slug: new content → new fingerprint → the approved page gets a superseding draft.
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: tokens are RS256 JWTs rotated on every use", "m1", "m2")];
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        Assert.Equal(1, drafts.Count);   // refreshed in place, not duplicated

        var superseding = Assert.Single(await svc.ListPendingAsync("repo-a"));
        Assert.True(superseding.ReplacesExisting);

        var a2 = await svc.ApproveAsync(superseding.Id);
        Assert.True(a2.Success);
        Assert.Equal(2, mint.CallCount);
        // The second mint carries Supersedes = the first minted memory id.
        Assert.Equal(a1.MintedMemoryId, mint.LastMint.SupersedesCanonId);
    }

    [Fact]
    public async Task Approve_AlreadyApproved_IsIdempotentNoOp_DoesNotRemint()
    {
        var clock = new FakeTimeProvider(T0);
        var drafts = new InMemoryCanonDraftStore();
        var mint = new RecordingCanonMintPort();
        var source = new ScriptedCanonDraftSource
        {
            Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: short-lived RS256 JWT session tokens", "m1")],
        };
        var svc = new CanonService(drafts, mint, [source], new InMemoryEidetStore(), clock);

        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var draft = Assert.Single(await svc.ListPendingAsync("repo-a"));

        var first = await svc.ApproveAsync(draft.Id);
        Assert.True(first.Success);

        var second = await svc.ApproveAsync(draft.Id);
        Assert.True(second.Success);
        Assert.Equal(first.MintedMemoryId, second.MintedMemoryId);   // same id echoed back
        Assert.Equal(1, mint.CallCount);                            // never minted twice
    }

    // ─── 2. Concurrency: exactly one mint under a claim race ─────────────

    [Fact]
    public async Task Approve_TwoConcurrent_ClaimSerializes_MintsExactlyOnce()
    {
        var clock = new FakeTimeProvider(T0);
        var drafts = new InMemoryCanonDraftStore();   // atomic lock-based TryClaimForApproveAsync
        var mint = new GatedCanonMintPort();
        var source = new ScriptedCanonDraftSource
        {
            Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: short-lived RS256 JWT session tokens", "m1")],
        };
        var svc = new CanonService(drafts, mint, [source], new InMemoryEidetStore(), clock);

        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var draft = Assert.Single(await svc.ListPendingAsync("repo-a"));

        // Approver A wins the claim then suspends inside MintAsync (gate held) — the doc is now Approving.
        var a = svc.ApproveAsync(draft.Id);
        await mint.Entered;

        // Approver B runs while A is mid-mint: its claim must lose, it must NOT mint, and it is rejected.
        var b = await svc.ApproveAsync(draft.Id);
        Assert.False(b.Success);
        Assert.Equal("approve already in progress", b.Reason);
        Assert.Equal(1, mint.CallCount);   // B never entered MintAsync

        mint.Release();
        var ra = await a;
        Assert.True(ra.Success);
        Assert.Equal(mint.MemoryId, ra.MintedMemoryId);
        Assert.Equal(1, mint.CallCount);   // exactly one mint across both approvers
    }

    [Fact]
    public async Task Approve_RejectedDraft_ReturnsDistinctReason_NeverMints()
    {
        var (svc, _, source, mint, _) = NewDamperFixture();
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: short-lived RS256 JWT session tokens", "m1")];
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var d = Assert.Single(await svc.ListPendingAsync("repo-a"));
        Assert.True((await svc.RejectAsync(d.Id, "too vague")).Success);

        // A rejected draft is refused with its actual state, not the misleading "already in progress".
        var r = await svc.ApproveAsync(d.Id);
        Assert.False(r.Success);
        Assert.Contains("rejected", r.Reason);
        Assert.DoesNotContain("in progress", r.Reason);
        Assert.Equal(0, mint.CallCount);
    }

    [Fact]
    public async Task Approve_LostClaim_DraftStillPending_RetriesBounded_AndSucceeds()
    {
        // A peer that claims then releases (failed mint) leaves the draft Pending: a lost claim that
        // re-reads Pending must be retried — bounded — not reported as "in progress".
        var clock = new FakeTimeProvider(T0);
        var drafts = new FlakyClaimCanonDraftStore(failures: 2);   // succeeds on the 3rd (== MaxClaimAttempts)
        var mint = new RecordingCanonMintPort();
        var source = new ScriptedCanonDraftSource
        {
            Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: short-lived RS256 JWT session tokens", "m1")],
        };
        var svc = new CanonService(drafts, mint, [source], new InMemoryEidetStore(), clock);

        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var d = Assert.Single(await svc.ListPendingAsync("repo-a"));

        var r = await svc.ApproveAsync(d.Id);
        Assert.True(r.Success);
        Assert.Equal(3, drafts.ClaimAttempts);
        Assert.Equal(1, mint.CallCount);
    }

    [Fact]
    public async Task Approve_LostClaim_RetriesExhausted_ReportsClaimLost_NeverMints()
    {
        var clock = new FakeTimeProvider(T0);
        var drafts = new FlakyClaimCanonDraftStore(failures: 99);   // never claimable within the bound
        var mint = new RecordingCanonMintPort();
        var source = new ScriptedCanonDraftSource
        {
            Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: short-lived RS256 JWT session tokens", "m1")],
        };
        var svc = new CanonService(drafts, mint, [source], new InMemoryEidetStore(), clock);

        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var d = Assert.Single(await svc.ListPendingAsync("repo-a"));

        var r = await svc.ApproveAsync(d.Id);
        Assert.False(r.Success);
        Assert.Equal("approve already in progress", r.Reason);
        Assert.Equal(3, drafts.ClaimAttempts);   // bounded — no livelock
        Assert.Equal(0, mint.CallCount);
    }

    // ─── 3. Damper matrix ───────────────────────────────────────────────

    [Fact]
    public async Task Damper_IdenticalPending_IsSkipped()
    {
        var (svc, drafts, source, _, clock) = NewDamperFixture();
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: short-lived RS256 JWT session tokens", "m1")];
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var first = await drafts.FindBySlugAsync("repo-a", CanonKind.Term, "auth");
        // Snapshot values (the fake returns a live reference; a refresh would mutate `first` in place).
        var firstFingerprint = first!.Fingerprint;
        var firstProposedAt = first.ProposedAt;

        // Advance the clock: had the identical candidate wrongly refreshed, ProposedAt would move to now.
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await svc.RegenerateDraftsAsync("repo-a"));   // byte-identical candidate → no churn
        Assert.Equal(1, drafts.Count);
        var again = await drafts.FindBySlugAsync("repo-a", CanonKind.Term, "auth");
        Assert.Equal(firstFingerprint, again!.Fingerprint);
        Assert.Equal(firstProposedAt, again.ProposedAt);   // not refreshed
    }

    [Fact]
    public async Task Damper_DriftedPending_RefreshesInPlace_SameDraftId()
    {
        var (svc, drafts, source, _, clock) = NewDamperFixture();
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: original definition of the token flow", "m1")];
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var before = await drafts.FindBySlugAsync("repo-a", CanonKind.Term, "auth");
        // Snapshot values (the fake returns a live reference; the refresh mutates `before` in place).
        var beforeId = before!.Id;
        var beforeFingerprint = before.Fingerprint;

        clock.Advance(TimeSpan.FromHours(1));
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: revised definition with RS256 and rotation", "m1", "m2")];
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));

        Assert.Equal(1, drafts.Count);   // one document, refreshed — not duplicated
        var after = await drafts.FindBySlugAsync("repo-a", CanonKind.Term, "auth");
        Assert.Equal(beforeId, after!.Id);
        Assert.NotEqual(beforeFingerprint, after.Fingerprint);
        Assert.Contains("revised definition", after.ProposedContent);
        Assert.Equal(new[] { "m1", "m2" }, after.MemberIds);
        Assert.Equal(CanonDraftStatus.Pending, after.Status);
    }

    [Fact]
    public async Task Damper_RejectedWithinCooldown_NotReproposed_EvenWithChangedFingerprint()
    {
        var (svc, drafts, source, _, clock) = NewDamperFixture();
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: first take on the token flow", "m1")];
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var d = Assert.Single(await svc.ListPendingAsync("repo-a"));
        Assert.True((await svc.RejectAsync(d.Id, "too vague")).Success);

        clock.Advance(TimeSpan.FromDays(1));   // still inside the 7-day cooldown
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: a completely different take on the token flow", "m1", "m2")];
        Assert.Equal(0, await svc.RegenerateDraftsAsync("repo-a"));

        var stored = await drafts.FindBySlugAsync("repo-a", CanonKind.Term, "auth");
        Assert.Equal(CanonDraftStatus.Rejected, stored!.Status);
        Assert.Empty(await svc.ListPendingAsync("repo-a"));
    }

    [Fact]
    public async Task Damper_RejectedAfterCooldown_WithFingerprintChange_IsReproposed()
    {
        var (svc, drafts, source, _, clock) = NewDamperFixture();
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: first take on the token flow", "m1")];
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var d = Assert.Single(await svc.ListPendingAsync("repo-a"));
        await svc.RejectAsync(d.Id, "too vague");

        clock.Advance(TimeSpan.FromDays(8));   // past the 7-day cooldown
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: reworked with RS256 and rotation detail", "m1", "m2")];
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));

        var reopened = Assert.Single(await svc.ListPendingAsync("repo-a"));
        Assert.Equal(d.Id, reopened.Id);
        var stored = await drafts.FindBySlugAsync("repo-a", CanonKind.Term, "auth");
        Assert.Equal(CanonDraftStatus.Pending, stored!.Status);
        Assert.Null(stored.RejectReason);
        Assert.Null(stored.CooldownUntil);
    }

    [Fact]
    public async Task Damper_RejectedAfterCooldown_UnchangedFingerprint_NotReproposed()
    {
        var (svc, drafts, source, _, clock) = NewDamperFixture();
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: the one and only take on the token flow", "m1")];
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var d = Assert.Single(await svc.ListPendingAsync("repo-a"));
        await svc.RejectAsync(d.Id, "not now");

        clock.Advance(TimeSpan.FromDays(8));   // cooldown elapsed, but the candidate is byte-identical
        Assert.Equal(0, await svc.RegenerateDraftsAsync("repo-a"));

        var stored = await drafts.FindBySlugAsync("repo-a", CanonKind.Term, "auth");
        Assert.Equal(CanonDraftStatus.Rejected, stored!.Status);
        Assert.Empty(await svc.ListPendingAsync("repo-a"));
    }

    [Fact]
    public async Task Damper_ApprovedSlug_UnchangedNoop_ThenDriftQueuesSupersedingDraft()
    {
        var (svc, drafts, source, _, _) = NewDamperFixture();
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: the approved token flow definition", "m1")];
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var d = Assert.Single(await svc.ListPendingAsync("repo-a"));
        var approve = await svc.ApproveAsync(d.Id);
        Assert.True(approve.Success);
        var mintedId = approve.MintedMemoryId;

        // Regenerate with the SAME candidate: the approved page is still current — nothing queued.
        Assert.Equal(0, await svc.RegenerateDraftsAsync("repo-a"));
        Assert.Empty(await svc.ListPendingAsync("repo-a"));

        // Drift it: a superseding draft is queued over the live page, carrying its minted id.
        source.Candidates = [CanonCandidates.Term("auth", "Auth", "Auth: the token flow, now with rotation on use", "m1", "m2")];
        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));

        var superseding = Assert.Single(await svc.ListPendingAsync("repo-a"));
        Assert.True(superseding.ReplacesExisting);
        var stored = await drafts.FindBySlugAsync("repo-a", CanonKind.Term, "auth");
        Assert.Equal(CanonDraftStatus.Pending, stored!.Status);
        Assert.Equal(mintedId, stored.SupersedesCanonId);
        Assert.Null(stored.MintedMemoryId);
    }

    // ─── 4. Degraded citation ───────────────────────────────────────────

    [Fact]
    public async Task GetDraft_MemberDeletedOrFaulting_DegradesToPlaceholderCitation_NoThrow()
    {
        var clock = new FakeTimeProvider(T0);
        var drafts = new InMemoryCanonDraftStore();
        var memStore = new ThrowingGetEidetStore(throwId: "memories/repo-a/insight/boom");

        var live = new MemoryEntry
        {
            Id = "memories/repo-a/insight/live",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "The auth service validates RS256 JWTs",
            OneLiner = "auth validates RS256 JWTs",
            Importance = 0.8f,
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
        };
        await memStore.StoreAsync(live);

        var svc = new CanonService(
            drafts, new RecordingCanonMintPort(), [new ScriptedCanonDraftSource()], memStore, clock);

        // Draft cites: one live member, one faulting read, one missing (deleted) member.
        var draft = new CanonDraft
        {
            Id = CanonDraftId.For("repo-a", CanonKind.Term, "auth"),
            RepoId = "repo-a",
            Kind = CanonKind.Term,
            Slug = "auth",
            Title = "Auth",
            ProposedContent = "Auth: token flow",
            MemberIds = [live.Id, "memories/repo-a/insight/boom", "memories/repo-a/insight/missing"],
            Fingerprint = "fp",
            ProposedAt = clock.GetUtcNow(),
            Status = CanonDraftStatus.Pending,
        };
        await drafts.StoreAsync(draft);

        var detail = await svc.GetDraftAsync(draft.Id);
        Assert.NotNull(detail);
        Assert.Equal(3, detail!.Citations.Count);   // order preserved: live, faulting, missing

        var liveCite = detail.Citations[0];
        Assert.Equal(live.Id, liveCite.MemoryId);
        Assert.Equal("auth validates RS256 JWTs", liveCite.OneLiner);
        Assert.Equal(MemoryType.Insight, liveCite.Type);
        Assert.Equal(0.8f, liveCite.Importance);
        Assert.Equal("#memory/" + live.Id, liveCite.Href);

        // Both the faulting read and the missing member degrade to the same placeholder — never a throw.
        foreach (var degraded in detail.Citations.Skip(1))
        {
            Assert.Equal("(memory no longer available)", degraded.OneLiner);
            Assert.Equal(0f, degraded.Importance);
            Assert.Equal("#memory/" + degraded.MemoryId, degraded.Href);
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static (CanonService svc, InMemoryCanonDraftStore drafts, ScriptedCanonDraftSource source,
        RecordingCanonMintPort mint, FakeTimeProvider clock) NewDamperFixture()
    {
        var clock = new FakeTimeProvider(T0);
        var drafts = new InMemoryCanonDraftStore();
        var mint = new RecordingCanonMintPort();
        var source = new ScriptedCanonDraftSource();
        var svc = new CanonService(drafts, mint, [source], new InMemoryEidetStore(), clock);
        return (svc, drafts, source, mint, clock);
    }
}
