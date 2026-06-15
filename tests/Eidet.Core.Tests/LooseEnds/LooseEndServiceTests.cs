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
