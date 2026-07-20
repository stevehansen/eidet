using Eidet.Core.Canon;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Canon;

/// <summary>
/// The one Canon test that mints through the REAL <see cref="MemoryServiceCanonAdapter"/> +
/// <see cref="MemoryService"/> over the in-memory <c>IEidetStore</c> — so the "full gate" write path is
/// verified, not mocked. Proves an approved draft lands as a real <c>MemoryEntry</c> that passed the
/// secret/signal gate and carries the assembled canon payload: <c>canon:term:&lt;slug&gt;</c> tag,
/// <c>DerivedFrom</c> = the member snapshot, <c>Source="canon-review"</c>, and anti-laundering provenance.
/// </summary>
public class CanonGateIntegrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Approve_ThroughRealMemoryService_MintsGatedCanonMemory_WithLineageAndTag()
    {
        var clock = new FakeTimeProvider(T0);
        var memStore = new InMemoryEidetStore();
        var memory = new MemoryService(memStore);

        // Two real, gate-passing member memories (source "claude-session" → AgentInferred, fully trusted).
        var m1 = await memory.StoreAsync("repo-a",
            "The auth service issues short-lived RS256 JWT tokens per session", MemoryType.Insight,
            ["auth", "jwt"], 0.8f);
        var m2 = await memory.StoreAsync("repo-a",
            "Auth tokens are validated with RS256 public keys on every request", MemoryType.Insight,
            ["auth", "security"], 0.6f);
        Assert.True(m1.Success);
        Assert.True(m2.Success);

        var adapter = new MemoryServiceCanonAdapter(memory, memStore);
        var drafts = new InMemoryCanonDraftStore();
        var source = new ScriptedCanonDraftSource
        {
            Candidates =
            [
                CanonCandidates.Term("auth", "Auth",
                    "Auth: the service issues short-lived RS256 JWT session tokens, rotated on each use",
                    m1.Id!, m2.Id!),
            ],
        };
        var svc = new CanonService(drafts, adapter, [source], memStore, clock);

        Assert.Equal(1, await svc.RegenerateDraftsAsync("repo-a"));
        var draft = Assert.Single(await svc.ListPendingAsync("repo-a"));

        var approve = await svc.ApproveAsync(draft.Id);
        Assert.True(approve.Success);
        Assert.NotNull(approve.MintedMemoryId);

        // The minted memory actually landed in the store, having passed the write gate.
        var minted = await memStore.GetAsync(approve.MintedMemoryId!);
        Assert.NotNull(minted);
        Assert.Equal(MemoryType.Insight, minted!.Type);
        Assert.Equal("canon-review", minted.Source);
        Assert.Contains("canon:term:auth", minted.Tags);
        Assert.Contains("auth", minted.Tags);   // member-defining tag inherited

        // DerivedFrom carries the full member snapshot (the whole point of the StoreOptions.DerivedFrom edit).
        Assert.Equal(2, minted.DerivedFrom.Count);
        Assert.Contains(m1.Id!, minted.DerivedFrom);
        Assert.Contains(m2.Id!, minted.DerivedFrom);

        // Both contributors are trusted → anti-laundering provenance is Consolidation; importance follows
        // the strongest contributor (0.8), clamped to the canon floor.
        Assert.Equal(MemoryProvenance.Consolidation, minted.Provenance);
        Assert.Equal(0.8f, minted.Importance);
        Assert.Contains("RS256", minted.Content);

        // And it is recallable through the normal pipeline — proof it truly entered memories/*.
        var recalled = await memory.RecallAsync("repo-a", "RS256 JWT session tokens");
        Assert.Contains(recalled, r => r.Id == approve.MintedMemoryId);
    }
}
