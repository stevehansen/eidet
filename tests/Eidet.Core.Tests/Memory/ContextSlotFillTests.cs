using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// The authority on wake-up slot fill: a per-type budget is a share of a FULL wake-up, so a type the
/// candidate pool cannot supply must not hold its slots empty.
///
/// Measured across 87 local repos before the clamp: 1,179 of 1,740 slots were filled, and 23 repos
/// rendered exactly 13 of 20 lines — the insight share — because they owned no procedures and no
/// heuristics at all, leaving 7 slots reserved for types that did not exist.
///
/// The clamp hands the remainder to insights only. Heuristics are action-shaped and net-negative-if-wrong
/// just like procedures, which is why the procedure cap already routed its freed slots to insights; these
/// tests pin that the fill does not become an excuse to relax either bound.
/// </summary>
public class ContextSlotFillTests
{
    private const string Repo = "fill-repo";

    /// <summary>Distinct vocabulary per entry, so the rendered-line duplicate filter cannot collapse
    /// them and let a test pass for the wrong reason.</summary>
    private static MemoryEntry Entry(string id, MemoryType type, int i, float importance) => new()
    {
        Id = $"memories/{Repo}/{type.ToString().ToLowerInvariant()}/{id}",
        RepoId = Repo,
        Type = type,
        Source = "claude-session",
        Content = $"{type} finding: " + string.Join(' ', Enumerable.Range(0, 5).Select(k => $"term{i}x{k}")),
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
        Importance = importance,
    };

    private static int Count(string context, MemoryType type) =>
        context.Split('\n').Count(l => l.Contains($"{type} finding:", StringComparison.Ordinal));

    private static int Items(string context) =>
        context.Split('\n').Count(l =>
        {
            var t = l.TrimStart();
            return t.StartsWith("[I]") || t.StartsWith("[P]") || t.StartsWith("[H]");
        });

    private static async Task<string> ContextAsync(InMemoryEidetStore store) =>
        await new MemoryService(store).GetContextAsync(Repo, maxTokens: 4000);

    [Fact]
    public async Task GetContext_fills_every_slot_for_a_repo_holding_only_insights()
    {
        var store = new InMemoryEidetStore();
        for (var i = 0; i < 30; i++)
            await store.StoreAsync(Entry($"i{i}", MemoryType.Insight, i, 0.90f));

        var context = await ContextAsync(store);

        // 20, not 13: the procedure and heuristic shares cannot be spent by a repo that owns neither.
        Assert.Equal(20, Items(context));
        Assert.Equal(20, Count(context, MemoryType.Insight));
    }

    [Fact]
    public async Task GetContext_gives_an_absent_types_slots_to_insights_not_heuristics()
    {
        var store = new InMemoryEidetStore();
        // No procedures at all, and heuristics ranked above insights so they would win any slot they
        // were allowed to take. They must still stop at their own share of 4.
        for (var i = 0; i < 30; i++)
            await store.StoreAsync(Entry($"h{i}", MemoryType.Heuristic, i, 0.95f));
        for (var i = 0; i < 30; i++)
            await store.StoreAsync(Entry($"i{i}", MemoryType.Insight, 100 + i, 0.90f));

        var context = await ContextAsync(store);

        Assert.Equal(20, Items(context));
        Assert.Equal(4, Count(context, MemoryType.Heuristic));
        Assert.Equal(16, Count(context, MemoryType.Insight));
    }

    [Fact]
    public async Task GetContext_still_hard_caps_procedures_when_the_pool_is_full_of_them()
    {
        var store = new InMemoryEidetStore();
        // Procedures outrank everything, so only the hard cap can hold them to 3.
        for (var i = 0; i < 30; i++)
            await store.StoreAsync(Entry($"p{i}", MemoryType.Procedure, i, 0.99f));
        for (var i = 0; i < 30; i++)
            await store.StoreAsync(Entry($"i{i}", MemoryType.Insight, 100 + i, 0.90f));
        for (var i = 0; i < 30; i++)
            await store.StoreAsync(Entry($"h{i}", MemoryType.Heuristic, 200 + i, 0.90f));

        var context = await ContextAsync(store);

        Assert.Equal(20, Items(context));
        Assert.Equal(3, Count(context, MemoryType.Procedure));
        Assert.Equal(4, Count(context, MemoryType.Heuristic));
        Assert.Equal(13, Count(context, MemoryType.Insight));
    }

    [Fact]
    public async Task GetContext_skips_an_oversized_line_instead_of_ending_the_wake_up()
    {
        var store = new InMemoryEidetStore();
        // A memory with no one-liner renders its whole body. This one outranks everything, and a single
        // one of them used to truncate the wake-up: a real repo rendered 6 of 20 lines behind a 28,890-
        // character steps Procedure.
        var huge = Entry("huge", MemoryType.Insight, 999, 0.99f);
        huge.Content = new string('x', 40_000);
        await store.StoreAsync(huge);
        for (var i = 0; i < 30; i++)
            await store.StoreAsync(Entry($"i{i}", MemoryType.Insight, i, 0.90f));

        var context = await ContextAsync(store);

        Assert.Equal(20, Items(context));
        Assert.DoesNotContain(new string('x', 200), context);
    }

    [Fact]
    public async Task GetContext_clamps_a_partially_supplied_type_to_what_exists()
    {
        var store = new InMemoryEidetStore();
        // One procedure and one heuristic: their shares shrink to 1 each and the other 5 reserved slots
        // go to insights rather than staying empty.
        await store.StoreAsync(Entry("p0", MemoryType.Procedure, 0, 0.95f));
        await store.StoreAsync(Entry("h0", MemoryType.Heuristic, 1, 0.95f));
        for (var i = 0; i < 30; i++)
            await store.StoreAsync(Entry($"i{i}", MemoryType.Insight, 100 + i, 0.90f));

        var context = await ContextAsync(store);

        Assert.Equal(20, Items(context));
        Assert.Equal(1, Count(context, MemoryType.Procedure));
        Assert.Equal(1, Count(context, MemoryType.Heuristic));
        Assert.Equal(18, Count(context, MemoryType.Insight));
    }
}
