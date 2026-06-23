using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Tests for the tiered fizzle penalty (issue #35) on <see cref="MemoryService.FeedbackAsync"/>.
/// A fizzle optionally carries a <see cref="FizzleReason"/>: content-invalidating reasons
/// (VersionDrift/Incorrect) cut Importance -0.2 / Confidence -0.3; everything else (WrongContext,
/// Other, null) cuts -0.1 / -0.15. Echo is unchanged (+0.05/+0.1) and ignores the reason. The
/// reason is recorded on <see cref="MemoryEntry.LastFizzleReason"/>. Driven through the shared
/// <see cref="InMemoryEidetStore"/> like the boundary tests.
/// </summary>
public class RoiFeedbackTests
{
    private static MemoryEntry Entry(string id, float importance = 0.6f, float confidence = 0.6f, double? lastLexShare = null) => new()
    {
        Id = id,
        RepoId = "repo-a",
        Type = MemoryType.Procedure,
        Content = id,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
        Importance = importance,
        Confidence = confidence,
        LastLexShare = lastLexShare,
    };

    private static async Task<MemoryEntry> SeedAsync(InMemoryEidetStore store, MemoryEntry entry)
    {
        await store.StoreAsync(entry);
        return entry;
    }

    // ─── Content-invalidating fizzle → steeper penalty ────────────────────────

    [Theory]
    [InlineData(FizzleReason.VersionDrift)]
    [InlineData(FizzleReason.Incorrect)]
    public async Task ContentInvalidating_fizzle_cuts_importance_by_point_two_and_confidence_by_point_three(FizzleReason reason)
    {
        var store = new InMemoryEidetStore();
        var entry = await SeedAsync(store, Entry("m1", importance: 0.6f, confidence: 0.6f));

        var ok = await new MemoryService(store).FeedbackAsync("m1", wasUsed: false, reason);

        Assert.True(ok);
        Assert.Equal(0.4f, entry.Importance, 0.0001f);   // 0.6 - 0.2
        Assert.Equal(0.3f, entry.Confidence, 0.0001f);   // 0.6 - 0.3
        Assert.Equal(reason, entry.LastFizzleReason);
        Assert.Equal(1, entry.FizzleCount);
    }

    // ─── Plain fizzle (WrongContext / Other / null) → shallow penalty ─────────

    [Theory]
    [InlineData(FizzleReason.WrongContext)]
    [InlineData(FizzleReason.Other)]
    public async Task NonContentInvalidating_fizzle_cuts_importance_by_point_one_and_confidence_by_point_one_five(FizzleReason reason)
    {
        var store = new InMemoryEidetStore();
        var entry = await SeedAsync(store, Entry("m1", importance: 0.6f, confidence: 0.6f));

        await new MemoryService(store).FeedbackAsync("m1", wasUsed: false, reason);

        Assert.Equal(0.5f, entry.Importance, 0.0001f);    // 0.6 - 0.1
        Assert.Equal(0.45f, entry.Confidence, 0.0001f);   // 0.6 - 0.15
        Assert.Equal(reason, entry.LastFizzleReason);
    }

    [Fact]
    public async Task Null_reason_fizzle_uses_the_shallow_penalty_and_records_null()
    {
        var store = new InMemoryEidetStore();
        var entry = await SeedAsync(store, Entry("m1", importance: 0.6f, confidence: 0.6f));

        await new MemoryService(store).FeedbackAsync("m1", wasUsed: false, reason: null);

        Assert.Equal(0.5f, entry.Importance, 0.0001f);
        Assert.Equal(0.45f, entry.Confidence, 0.0001f);
        Assert.Null(entry.LastFizzleReason);
    }

    // ─── Echo is unchanged by the new contract; reason ignored ────────────────

    [Fact]
    public async Task Echo_raises_importance_and_confidence_and_ignores_reason()
    {
        var store = new InMemoryEidetStore();
        var entry = await SeedAsync(store, Entry("m1", importance: 0.6f, confidence: 0.6f));

        // A reason on an echo must be a no-op for the reason field — echo isn't a fizzle.
        await new MemoryService(store).FeedbackAsync("m1", wasUsed: true, reason: FizzleReason.Incorrect);

        Assert.Equal(0.65f, entry.Importance, 0.0001f);   // 0.6 + 0.05
        Assert.Equal(0.7f, entry.Confidence, 0.0001f);    // 0.6 + 0.1
        Assert.Equal(1, entry.EchoCount);
        Assert.Equal(0, entry.FizzleCount);
        Assert.Null(entry.LastFizzleReason); // echo path never sets LastFizzleReason
    }

    // ─── Clamps ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Importance_never_falls_below_floor_of_point_zero_five()
    {
        var store = new InMemoryEidetStore();
        // Start at the lowest legal importance; a content-invalidating fizzle (-0.2) must clamp at 0.05.
        var entry = await SeedAsync(store, Entry("m1", importance: 0.1f, confidence: 0.5f));

        await new MemoryService(store).FeedbackAsync("m1", wasUsed: false, FizzleReason.VersionDrift);

        Assert.Equal(0.05f, entry.Importance, 0.0001f);
    }

    [Fact]
    public async Task Confidence_never_falls_below_zero()
    {
        var store = new InMemoryEidetStore();
        var entry = await SeedAsync(store, Entry("m1", importance: 0.6f, confidence: 0.1f));

        await new MemoryService(store).FeedbackAsync("m1", wasUsed: false, FizzleReason.Incorrect);

        Assert.Equal(0.0f, entry.Confidence, 0.0001f); // 0.1 - 0.3 clamps to 0
    }

    // ─── Regression guard: tiered penalty must not break #33 alpha learning ───

    /// <summary>
    /// A fizzle on a memory that carries <see cref="MemoryEntry.LastLexShare"/> (was surfaced under v2)
    /// must STILL run the #33 alpha-learning EWMA step after the tiered penalty. Uses the same
    /// <see cref="AlphaLearningStore"/> harness as <see cref="AlphaLearningTests"/> so the fold is
    /// observable; a high-lexShare fizzle pushes the learned alpha DOWN (the lexical mix misled).
    /// </summary>
    [Fact]
    public async Task Fizzle_with_lexShare_still_triggers_alpha_learning_and_records_reason()
    {
        var store = new AlphaLearningStore();
        store.Seed(new MemoryEntry
        {
            Id = "m1",
            RepoId = "repo-a",
            Type = MemoryType.Procedure,
            Content = "m1",
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
            Importance = 0.6f,
            Confidence = 0.6f,
            LastLexShare = 0.9,
        });

        Assert.Null(await store.GetRepoAlphaAsync("repo-a"));

        await new MemoryService(store).FeedbackAsync("m1", wasUsed: false, FizzleReason.VersionDrift);

        // Alpha-learning step ran exactly once and moved alpha below the 0.5 default (high lexShare fizzle).
        Assert.Equal(1, store.AlphaUpdateCalls);
        var alpha = await store.GetRepoAlphaAsync("repo-a");
        Assert.NotNull(alpha);
        Assert.True(alpha < 0.5, $"high-lexShare fizzle should pull alpha below default, got {alpha}");

        // And the tiered penalty + reason still applied to the entry.
        var entry = store.GetEntry("m1")!;
        Assert.Equal(FizzleReason.VersionDrift, entry.LastFizzleReason);
        Assert.Equal(0.4f, entry.Importance, 0.0001f);
        Assert.Equal(0.3f, entry.Confidence, 0.0001f);
    }
}
