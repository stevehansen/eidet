using Eidet.Core.Domain;
using Eidet.Core.LooseEnds;
using Eidet.Core.LooseEnds.Promotion;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.LooseEnds;

/// <summary>
/// End-to-end and invariant tests for the Loose End feature, driven entirely through
/// <see cref="LooseEndService"/> over the in-memory ports + a deterministic clock. Covers the
/// canonical cases named in LooseEndSpec.md §Implementation Sketch → Tests and the §Write Path /
/// §Lifecycle / §Surfacing invariants.
/// </summary>
public class LooseEndServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    // ─── 1. End-to-end happy path (real promotion adapter) ──────────────

    [Fact]
    public async Task Park_Surface_PromoteViaRealAdapter_MintsGatedMemory_AndDropsFromSlice()
    {
        var clock = new FakeTimeProvider(T0);
        var endStore = new InMemoryLooseEndStore();
        var memStore = new InMemoryEidetStore();
        var memory = new MemoryService(memStore);
        var promote = new MemoryServicePromotionAdapter(memory);
        var svc = new LooseEndService(endStore, promote, clock);

        var note = "Possible race in the retry backoff path under high concurrency, revisit later";
        var parked = await svc.ParkAsync("repo-a", note);
        Assert.True(parked.Success);
        Assert.NotNull(parked.Id);

        // The open Loose End surfaces in the wake-up slice with the [~] open-work prefix.
        var sliceBefore = await svc.RenderWakeupSliceAsync("repo-a", 600);
        Assert.Contains("[~] ", sliceBefore);
        Assert.Contains(note, sliceBefore);

        // Resolve as Promoted → re-enters the gated memory write path and mints a MemoryEntry.
        var resolved = await svc.ResolveAsync(parked.Id!, ResolutionKind.Promoted);
        Assert.True(resolved.Success);
        Assert.Equal(LooseEndState.Resolved, resolved.State);
        Assert.Equal(ResolutionKind.Promoted, resolved.Kind);
        Assert.NotNull(resolved.PromotedToMemoryId);

        // The minted memory is now in the memory store and recallable.
        var recalled = await memory.RecallAsync("repo-a", "retry backoff race");
        Assert.Single(recalled);
        Assert.Equal(resolved.PromotedToMemoryId, recalled[0].Id);

        // PromotedToMemoryId is persisted on the resolved Loose End.
        var stored = await endStore.GetAsync(parked.Id!);
        Assert.NotNull(stored);
        Assert.Equal(resolved.PromotedToMemoryId, stored!.PromotedToMemoryId);

        // The resolved end no longer surfaces in the wake-up slice.
        var sliceAfter = await svc.RenderWakeupSliceAsync("repo-a", 600);
        Assert.DoesNotContain(note, sliceAfter);
    }

    // ─── 2. Gate split — secret rejected ────────────────────────────────

    [Fact]
    public async Task Park_SecretNote_IsRejected_AndNotStored()
    {
        var svc = NewService(out var endStore, out _, new FakeTimeProvider(T0));

        var result = await svc.ParkAsync("repo-a", "deploy key is AKIAIOSFODNN7EXAMPLE for the staging bucket");

        Assert.False(result.Success);
        Assert.Null(result.Id);
        Assert.Contains("AWS access key", result.Reason);
        Assert.Equal(0, endStore.Count);
    }

    // ─── 3. Gate split — terse/self-talk accepted ───────────────────────

    [Theory]
    [InlineData("revisit this")]                        // under the 20-char signal floor
    [InlineData("i will fix the retry logic")]          // self-talk prefix
    public async Task Park_TerseOrSelfTalkNote_IsAccepted_SignalGateSkipped(string note)
    {
        var svc = NewService(out var endStore, out _, new FakeTimeProvider(T0));

        var result = await svc.ParkAsync("repo-a", note);

        Assert.True(result.Success);
        Assert.NotNull(result.Id);
        Assert.Equal(1, endStore.Count);
    }

    // ─── 4. Promote re-enters the full gate (real adapter) ──────────────

    [Fact]
    public async Task Resolve_PromoteLowSignalNote_RejectedAtMemoryGate_StaysOpen()
    {
        var clock = new FakeTimeProvider(T0);
        var endStore = new InMemoryLooseEndStore();
        var memory = new MemoryService(new InMemoryEidetStore());
        var promote = new MemoryServicePromotionAdapter(memory);
        var svc = new LooseEndService(endStore, promote, clock);

        // Terse self-talk parks fine (signal gate skipped) ...
        var parked = await svc.ParkAsync("repo-a", "revisit this");
        Assert.True(parked.Success);

        // ... but promote re-enters the full signal gate, which rejects it.
        var resolved = await svc.ResolveAsync(parked.Id!, ResolutionKind.Promoted);

        Assert.False(resolved.Success);
        Assert.NotNull(resolved.Reason);
        Assert.Null(resolved.PromotedToMemoryId);

        // Actual behavior: a rejected promote does NOT mark the end resolved — it stays open.
        var stored = await endStore.GetAsync(parked.Id!);
        Assert.NotNull(stored);
        Assert.Equal(LooseEndState.Open, stored!.State);
        Assert.Null(stored.Resolution);
        Assert.Null(stored.ResolvedAt);

        // It therefore still surfaces in the wake-up slice.
        var slice = await svc.RenderWakeupSliceAsync("repo-a", 600);
        Assert.Contains("revisit this", slice);
    }

    [Fact]
    public async Task Resolve_PromoteWithExternalRef_LinksWithoutMinting_AndEchoesRef()
    {
        var clock = new FakeTimeProvider(T0);
        var endStore = new InMemoryLooseEndStore();
        var memory = new MemoryService(new InMemoryEidetStore());
        var promote = new MemoryServicePromotionAdapter(memory);
        var svc = new LooseEndService(endStore, promote, clock);

        var parked = await svc.ParkAsync("repo-a", "track the upstream fix for the retry backoff race");

        var resolved = await svc.ResolveAsync(parked.Id!, ResolutionKind.Promoted,
            new ResolveOptions { ExternalRef = "gh#412" });

        // Link-only: the external ref is recorded and echoed back, and no memory is minted.
        Assert.True(resolved.Success);
        Assert.Equal("gh#412", resolved.ExternalRef);
        Assert.Null(resolved.PromotedToMemoryId);
        Assert.Empty(await memory.RecallAsync("repo-a", "retry backoff race"));

        var stored = await endStore.GetAsync(parked.Id!);
        Assert.Equal("gh#412", stored!.ExternalRef);
    }

    [Fact]
    public async Task Resolve_PromoteWithWhitespaceExternalRef_MintsMemory_NotBlankLink()
    {
        var clock = new FakeTimeProvider(T0);
        var endStore = new InMemoryLooseEndStore();
        var memory = new MemoryService(new InMemoryEidetStore());
        var promote = new MemoryServicePromotionAdapter(memory);
        var svc = new LooseEndService(endStore, promote, clock);

        var parked = await svc.ParkAsync("repo-a",
            "Possible race in the retry backoff path under high concurrency, revisit later");

        // A stray whitespace-only promote_to must be treated as absent — mint the memory, never
        // close the end as a link with a blank ref (which would silently drop the knowledge).
        var resolved = await svc.ResolveAsync(parked.Id!, ResolutionKind.Promoted,
            new ResolveOptions { ExternalRef = "   " });

        Assert.True(resolved.Success);
        Assert.NotNull(resolved.PromotedToMemoryId);
        Assert.Null(resolved.ExternalRef);
        Assert.Single(await memory.RecallAsync("repo-a", "retry backoff race"));
    }

    // ─── 5. Idempotent resolve ──────────────────────────────────────────

    [Fact]
    public async Task Resolve_Twice_IsNoOp_KeepsOriginalResolution_AndDoesNotRemint()
    {
        var clock = new FakeTimeProvider(T0);
        var svc = NewService(out var endStore, out var promote, clock);

        var parked = await svc.ParkAsync("repo-a", "tidy up the cache invalidation comments later");
        Assert.True(parked.Success);

        var first = await svc.ResolveAsync(parked.Id!, ResolutionKind.Done);
        Assert.True(first.Success);
        Assert.Equal(ResolutionKind.Done, first.Kind);
        var firstResolvedAt = (await endStore.GetAsync(parked.Id!))!.ResolvedAt;
        Assert.NotNull(firstResolvedAt);

        clock.Advance(TimeSpan.FromHours(1));

        // Second resolve, now as Promoted — must be a no-op returning current (Done) state.
        var second = await svc.ResolveAsync(parked.Id!, ResolutionKind.Promoted);

        Assert.True(second.Success);
        Assert.Equal(LooseEndState.Resolved, second.State);
        Assert.Equal(ResolutionKind.Done, second.Kind);    // unchanged — not re-labeled Promoted
        Assert.Null(second.PromotedToMemoryId);

        // No promotion was ever attempted — the promote port was never called, so no memory minted.
        Assert.Equal(0, promote.CallCount);

        // The original resolution kind and ResolvedAt are unchanged despite the advanced clock.
        var stored = await endStore.GetAsync(parked.Id!);
        Assert.Equal(ResolutionKind.Done, stored!.Resolution);
        Assert.Equal(firstResolvedAt, stored.ResolvedAt);
    }

    // ─── 5b. Concurrency / claim-before-promote saga (issue #46) ─────────

    [Fact]
    public async Task Resolve_TwoConcurrentPromotes_ClaimSerializes_PromotesExactlyOnce_NoDoubleMint()
    {
        var clock = new FakeTimeProvider(T0);
        var endStore = new InMemoryLooseEndStore();   // its TryClaimForResolveAsync is atomic under lock
        var promote = new GatedPromotionAdapter();
        var svc = new LooseEndService(endStore, promote, clock);

        var parked = await svc.ParkAsync("repo-a", "Possible race in the retry backoff path, revisit later");
        Assert.True(parked.Success);

        // Resolver A wins the claim then suspends inside PromoteAsync (gate held).
        var a = svc.ResolveAsync(parked.Id!, ResolutionKind.Promoted);

        // Wait until A is parked mid-promote (store doc is now Resolving) before launching B —
        // this deterministically forces B's claim to lose against the in-flight resolve.
        await promote.Entered;

        // Resolver B runs while A is still mid-promote. The end is Resolving, so B's claim must lose,
        // B must NOT call promote, and B must be rejected ("resolve already in progress").
        var b = await svc.ResolveAsync(parked.Id!, ResolutionKind.Promoted);
        Assert.False(b.Success);
        Assert.Equal("resolve already in progress", b.Reason);
        Assert.Equal(1, promote.CallCount);   // B never entered PromoteAsync

        // Release A; it finishes Resolved with the single minted id.
        promote.Release();
        var resolved = await a;

        Assert.True(resolved.Success);
        Assert.Equal(LooseEndState.Resolved, resolved.State);
        Assert.Equal(ResolutionKind.Promoted, resolved.Kind);
        Assert.Equal("memories/fake/insight/abc123", resolved.PromotedToMemoryId);

        // Promote was invoked EXACTLY once across both resolvers — no double-mint.
        Assert.Equal(1, promote.CallCount);

        // The single minted id is persisted; no orphan.
        var stored = await endStore.GetAsync(parked.Id!);
        Assert.Equal(LooseEndState.Resolved, stored!.State);
        Assert.Equal("memories/fake/insight/abc123", stored.PromotedToMemoryId);
    }

    [Fact]
    public async Task Resolve_RejectedPromote_ReleasesClaim_LeavesEndOpen_NotResolving()
    {
        var clock = new FakeTimeProvider(T0);
        var endStore = new InMemoryLooseEndStore();
        var promote = new InMemoryPromotionAdapter { Next = new PromotionResult(false, null, null, "promotion rejected") };
        var svc = new LooseEndService(endStore, promote, clock);

        var parked = await svc.ParkAsync("repo-a", "track the upstream fix for the retry backoff race");
        Assert.True(parked.Success);

        var resolved = await svc.ResolveAsync(parked.Id!, ResolutionKind.Promoted);

        Assert.False(resolved.Success);
        Assert.Equal("promotion rejected", resolved.Reason);
        Assert.Null(resolved.PromotedToMemoryId);

        // The claim was released: the end is back Open (NOT wedged in Resolving), unresolved.
        var stored = await endStore.GetAsync(parked.Id!);
        Assert.NotNull(stored);
        Assert.Equal(LooseEndState.Open, stored!.State);
        Assert.Null(stored.Resolution);
        Assert.Null(stored.ResolvedAt);

        // And it therefore reappears in the open surfaces.
        var slice = await svc.RenderWakeupSliceAsync("repo-a", 600);
        Assert.Contains("track the upstream fix for the retry backoff race", slice);
    }

    [Fact]
    public async Task Resolve_PromoteSucceedsButFinalWriteThrows_ReleasesCleanOpenEnd()
    {
        var clock = new FakeTimeProvider(T0);
        var inner = new InMemoryLooseEndStore();
        // Throw on the FIRST UpdateAsync (the final resolve write); the release write (2nd) succeeds.
        var endStore = new ThrowOnNthUpdateStore(inner, throwOnCall: 1);
        var promote = new InMemoryPromotionAdapter();   // default: promote succeeds, mints a memory id
        var svc = new LooseEndService(endStore, promote, clock);

        var parked = await svc.ParkAsync("repo-a", "track the upstream fix for the retry backoff race");
        Assert.True(parked.Success);

        // Promote succeeds, then the final write throws — the exception propagates (never swallowed).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResolveAsync(parked.Id!, ResolutionKind.Promoted));

        // The memory was minted exactly once (the orphan the ≥0.92 dedup absorbs on a later retry).
        Assert.Equal(1, promote.CallCount);

        // The claim was released to a CLEAN Open end — not wedged in Resolving, and carrying no
        // dangling resolution metadata despite the promote having succeeded before the write failed.
        var stored = await inner.GetAsync(parked.Id!);
        Assert.NotNull(stored);
        Assert.Equal(LooseEndState.Open, stored!.State);
        Assert.Null(stored.Resolution);
        Assert.Null(stored.ResolvedAt);
        Assert.Null(stored.PromotedToMemoryId);
        Assert.Null(stored.ExternalRef);
    }

    [Fact]
    public async Task Resolve_TokenCancelled_StillReleasesClaim_NotWedgedInResolving()
    {
        var clock = new FakeTimeProvider(T0);
        var inner = new InMemoryLooseEndStore();
        var endStore = new CancellationHonoringStore(inner);   // UpdateAsync honors ct, like RavenDB
        var promote = new ThrowingPromotionAdapter(new OperationCanceledException());
        var svc = new LooseEndService(endStore, promote, clock);

        var parked = await svc.ParkAsync("repo-a", "track the upstream fix for the retry backoff race");
        Assert.True(parked.Success);

        using var cts = new CancellationTokenSource();
        cts.Cancel();   // the caller's token is already cancelled when the resolve fails

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => svc.ResolveAsync(parked.Id!, ResolutionKind.Promoted, null, cts.Token));

        // The release used CancellationToken.None, so it ran despite the cancelled caller token —
        // the claim was undone and the end is Open, NOT wedged in Resolving.
        var stored = await inner.GetAsync(parked.Id!);
        Assert.Equal(LooseEndState.Open, stored!.State);
    }

    [Fact]
    public async Task Resolve_LostClaimThenEndReleasedToOpen_RetriesAndResolves()
    {
        var clock = new FakeTimeProvider(T0);
        var inner = new InMemoryLooseEndStore();
        var endStore = new ClaimFailsOnceStore(inner);   // first claim loses; end stays Open
        var promote = new InMemoryPromotionAdapter();
        var svc = new LooseEndService(endStore, promote, clock);

        var parked = await svc.ParkAsync("repo-a", "track the upstream fix for the retry backoff race");
        Assert.True(parked.Success);

        // The first claim loses to a peer that then released the end back to Open; the bounded retry
        // re-claims and resolves rather than falsely reporting "resolve already in progress".
        var resolved = await svc.ResolveAsync(parked.Id!, ResolutionKind.Promoted);

        Assert.True(resolved.Success);
        Assert.Equal(LooseEndState.Resolved, resolved.State);
        Assert.Equal(ResolutionKind.Promoted, resolved.Kind);
        Assert.Equal(1, promote.CallCount);
        Assert.Equal(LooseEndState.Resolved, (await inner.GetAsync(parked.Id!))!.State);
    }

    [Fact]
    public async Task TryClaimForResolve_OpenEnd_WinsOnce_ThenLoses_AndLosesForResolvedOrUnknown()
    {
        var clock = new FakeTimeProvider(T0);
        var svc = NewService(out var endStore, out _, clock);

        var parked = await svc.ParkAsync("repo-a", "some open work to claim for resolution");
        Assert.True(parked.Success);

        // First claim on an Open end wins (Open→Resolving); a second claim loses (now Resolving).
        Assert.True(await endStore.TryClaimForResolveAsync(parked.Id!));
        Assert.Equal(LooseEndState.Resolving, (await endStore.GetAsync(parked.Id!))!.State);
        Assert.False(await endStore.TryClaimForResolveAsync(parked.Id!));

        // A Resolved end can never be claimed.
        var resolvedPark = await svc.ParkAsync("repo-a", "other open work that gets fully resolved");
        var done = await svc.ResolveAsync(resolvedPark.Id!, ResolutionKind.Done);
        Assert.True(done.Success);
        Assert.Equal(LooseEndState.Resolved, (await endStore.GetAsync(resolvedPark.Id!))!.State);
        Assert.False(await endStore.TryClaimForResolveAsync(resolvedPark.Id!));

        // An unknown id can never be claimed.
        Assert.False(await endStore.TryClaimForResolveAsync("looseends/repo-a/deadbeefdead"));
    }

    // ─── 5c. Priority clamp (STRIDE T-10, #77) ──────────────────────────

    [Theory]
    [InlineData(0, 1)]          // below range → clamped up to high
    [InlineData(-5, 1)]         // negative → high
    [InlineData(int.MinValue, 1)]
    [InlineData(4, 3)]          // above range → clamped down to low
    [InlineData(999, 3)]
    [InlineData(int.MaxValue, 3)]
    [InlineData(1, 1)]          // in-range values pass through unchanged
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public async Task Park_ClampsPriorityToOneToThree(int requested, int expected)
    {
        var svc = NewService(out var endStore, out _, new FakeTimeProvider(T0));

        var parked = await svc.ParkAsync(new ParkOptions("repo-a", "some open work to revisit later")
        {
            Priority = requested,
        });
        Assert.True(parked.Success);

        var stored = await endStore.GetAsync(parked.Id!);
        Assert.Equal(expected, stored!.Priority);
    }

    // ─── 6. Wake-up cap & budget ────────────────────────────────────────

    [Fact]
    public async Task RenderWakeupSlice_CapsAtThree_WithPrefix_OldestFirstWithinTier()
    {
        var clock = new FakeTimeProvider(T0);
        var svc = NewService(out _, out _, clock);

        // Park 5 ends at the SAME priority; advance the clock between each so CreatedAt strictly
        // increases. Same-tier ordering is unambiguous: oldest-first (CreatedAt asc).
        async Task Park(string note)
        {
            await svc.ParkAsync(new ParkOptions("repo-a", note) { Priority = 2 });
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        await Park("oldest fix the flaky integration test");
        await Park("second review the cache eviction path");
        await Park("third audit the auth header parsing");
        await Park("fourth check the migration ordering bug");
        await Park("newest tidy the dead config flag");

        var slice = await svc.RenderWakeupSliceAsync("repo-a", 600);

        var lines = slice.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);                     // hard cap of 3

        Assert.All(lines, l => Assert.StartsWith("[~] ", l)); // distinct open-work prefix

        // Oldest-first within the tier: the three oldest parks, in creation order.
        Assert.Contains("oldest fix the flaky integration test", lines[0]);
        Assert.Contains("second review the cache eviction path", lines[1]);
        Assert.Contains("third audit the auth header parsing", lines[2]);

        // The two newest never made the cut.
        Assert.DoesNotContain("dead config flag", slice);
        Assert.DoesNotContain("migration ordering bug", slice);
    }

    [Fact]
    public async Task RenderWakeupSlice_SurfacesHighPriorityFirst()
    {
        var clock = new FakeTimeProvider(T0);
        var svc = NewService(out _, out _, clock);

        async Task Park(string note, int priority)
        {
            await svc.ParkAsync(new ParkOptions("repo-a", note) { Priority = priority });
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        await Park("normal middle review the cache eviction path", 2);
        await Park("low cleanup the dead config flag", 3);
        await Park("high audit the auth header parsing", 1);

        var slice = await svc.RenderWakeupSliceAsync("repo-a", 600);
        var lines = slice.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Intended: high (Priority 1) first, then normal (2), then low (3).
        Assert.Contains("high audit the auth header parsing", lines[0]);
        Assert.Contains("normal middle review the cache eviction path", lines[1]);
        Assert.Contains("low cleanup the dead config flag", lines[2]);
    }

    [Fact]
    public async Task RenderWakeupSlice_NeverExceedsTokenBudget()
    {
        var clock = new FakeTimeProvider(T0);
        var svc = NewService(out _, out _, clock);

        for (var i = 0; i < 5; i++)
        {
            await svc.ParkAsync("repo-a", $"this is a reasonably long parked note number {i} about some pending refactor work");
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        // A tight budget that fits at most one or two lines.
        const int tinyBudget = 25;
        var slice = await svc.RenderWakeupSliceAsync("repo-a", tinyBudget);

        var estimatedTokens = (int)Math.Ceiling(slice.Length / 4.0);
        Assert.True(estimatedTokens <= tinyBudget, $"slice spent {estimatedTokens} tokens, budget was {tinyBudget}");
    }

    [Fact]
    public async Task RenderWakeupSlice_NoOpenEnds_ReturnsEmpty()
    {
        var svc = NewService(out _, out _, new FakeTimeProvider(T0));
        var slice = await svc.RenderWakeupSliceAsync("repo-a", 600);
        Assert.Equal("", slice);
    }

    // ─── 7. Recall ride-along ───────────────────────────────────────────

    [Fact]
    public async Task RideAlong_ReturnsOnlyOpenEndsWithOverlappingTags()
    {
        var clock = new FakeTimeProvider(T0);
        var svc = NewService(out _, out _, clock);

        var authEnd = await svc.ParkAsync(new ParkOptions("repo-a", "tighten the auth token refresh window")
        {
            Tags = ["auth", "security"],
        });
        clock.Advance(TimeSpan.FromMinutes(1));

        await svc.ParkAsync(new ParkOptions("repo-a", "speed up the cache warm-up on cold start")
        {
            Tags = ["cache", "perf"],
        });
        clock.Advance(TimeSpan.FromMinutes(1));

        // A resolved end with a matching tag must be excluded.
        var resolvedEnd = await svc.ParkAsync(new ParkOptions("repo-a", "old auth note already handled")
        {
            Tags = ["auth"],
        });
        await svc.ResolveAsync(resolvedEnd.Id!, ResolutionKind.Done);

        var matches = await svc.RideAlongAsync("repo-a", ["auth"]);

        Assert.Single(matches);
        Assert.Equal(authEnd.Id, matches[0].Id);
    }

    [Fact]
    public async Task RideAlong_NoTags_ReturnsEmpty()
    {
        var svc = NewService(out _, out _, new FakeTimeProvider(T0));
        await svc.ParkAsync(new ParkOptions("repo-a", "some open work with a tag") { Tags = ["x"] });

        var matches = await svc.RideAlongAsync("repo-a", []);

        Assert.Empty(matches);
    }

    // ─── 8. ID determinism ──────────────────────────────────────────────

    [Fact]
    public void IdGenerator_IsStableForSameInputs_AndDiffersWhenAnyInputChanges()
    {
        var now = T0;

        var a = LooseEndIdGenerator.Generate("repo-a", "the note", now);
        var same = LooseEndIdGenerator.Generate("repo-a", "the note", now);
        Assert.Equal(a, same);

        Assert.NotEqual(a, LooseEndIdGenerator.Generate("repo-b", "the note", now));
        Assert.NotEqual(a, LooseEndIdGenerator.Generate("repo-a", "a different note", now));
        Assert.NotEqual(a, LooseEndIdGenerator.Generate("repo-a", "the note", now.AddSeconds(1)));
    }

    [Fact]
    public void IdGenerator_FormatIsLooseEndsRepoShortHash()
    {
        var id = LooseEndIdGenerator.Generate("repo-a", "the note", T0);

        var parts = id.Split('/');
        Assert.Equal(3, parts.Length);
        Assert.Equal("looseends", parts[0]);
        Assert.Equal("repo-a", parts[1]);
        Assert.Equal(12, parts[2].Length);
        Assert.Matches("^[0-9a-f]{12}$", parts[2]);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static LooseEndService NewService(
        out InMemoryLooseEndStore endStore, out InMemoryPromotionAdapter promote, TimeProvider clock)
    {
        endStore = new InMemoryLooseEndStore();
        promote = new InMemoryPromotionAdapter();
        return new LooseEndService(endStore, promote, clock);
    }
}
