using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// The L1 wake-up bounds consolidation's share at 6 of 20 slots.
///
/// Consolidation re-derives an insight from the same observation cluster on every scheduled run, so its
/// output is redundant by construction in a way no other source is. Measured on a real corpus: 97% of
/// duplicate wake-up lines were consolidation output, at a median word overlap of 0.25 — which is why
/// the rendered-line filter cannot catch them. It compares words, and a paraphrase shares few.
///
/// These tests pin the two halves of the fix: the cap binds, and the slots it frees are backfilled from
/// other sources rather than shortening the wake-up.
/// </summary>
public class ContextConsolidationCapTests
{
    private const string Repo = "repo-a";

    /// <summary>
    /// Distinct vocabulary per entry. Entries worded alike would be collapsed by the rendered-line
    /// duplicate filter, and the test could then pass with the cap removed.
    /// </summary>
    private static MemoryEntry Entry(string id, MemoryType type, string source, string marker, int i, float importance) => new()
    {
        Id = $"memories/{Repo}/{type.ToString().ToLowerInvariant()}/{id}",
        RepoId = Repo,
        Type = type,
        Source = source,
        Content = $"{marker} finding: " + string.Join(' ', Enumerable.Range(0, 5).Select(k => $"term{i}x{k}")),
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
        Importance = importance,
    };

    private static int Count(string context, string marker) =>
        context.Split('\n').Count(l => l.Contains($"{marker} finding:", StringComparison.Ordinal));

    private static int Items(string context) =>
        context.Split('\n').Count(l =>
        {
            var t = l.TrimStart();
            return t.StartsWith("[I]") || t.StartsWith("[P]") || t.StartsWith("[H]");
        });

    [Fact]
    public async Task GetContext_caps_consolidation_at_six_even_when_it_outscores_everything()
    {
        var store = new InMemoryEidetStore();
        // Consolidation outranks every other source, so without the cap it takes every insight slot.
        for (var i = 0; i < 20; i++)
            await store.StoreAsync(Entry($"c{i}", MemoryType.Insight, "consolidation", "Consolidated", i, 0.95f));
        for (var i = 0; i < 20; i++)
            await store.StoreAsync(Entry($"s{i}", MemoryType.Insight, "claude-session", "Session", 100 + i, 0.60f));

        var context = await new MemoryService(store).GetContextAsync(Repo, maxTokens: 4000);

        Assert.Equal(6, Count(context, "Consolidated"));
    }

    [Fact]
    public async Task GetContext_slots_freed_by_the_cap_backfill_from_other_sources()
    {
        var store = new InMemoryEidetStore();
        // Enough of both sources that the cap — not scarcity — decides the mix, and consolidation ranked
        // above session as it is on a real corpus: otherwise session fills the slots on its own and the
        // assertion would hold with the cap deleted.
        for (var i = 0; i < 20; i++)
            await store.StoreAsync(Entry($"c{i}", MemoryType.Insight, "consolidation", "Consolidated", i, 0.95f));
        for (var i = 0; i < 20; i++)
            await store.StoreAsync(Entry($"s{i}", MemoryType.Insight, "claude-session", "Session", 100 + i, 0.90f));
        for (var i = 0; i < 10; i++)
            await store.StoreAsync(Entry($"p{i}", MemoryType.Procedure, "claude-session", "Proc", 200 + i, 0.90f));
        for (var i = 0; i < 10; i++)
            await store.StoreAsync(Entry($"h{i}", MemoryType.Heuristic, "claude-session", "Heur", 300 + i, 0.90f));

        var context = await new MemoryService(store).GetContextAsync(Repo, maxTokens: 4000);

        // Same 20-line wake-up as before the cap existed: the 7 insight slots consolidation no longer
        // gets go to another source, they are not simply dropped. That is the whole point — a cap that
        // shortened the wake-up would trade duplication for silence.
        Assert.Equal(20, Items(context));
        Assert.Equal(6, Count(context, "Consolidated"));
        Assert.Equal(7, Count(context, "Session"));
    }

    [Fact]
    public async Task GetContext_does_not_cap_a_repo_whose_knowledge_is_all_session_sourced()
    {
        var store = new InMemoryEidetStore();
        // The asymmetry is deliberate: a symmetric per-source cap was measured to evict good lines from
        // repos like this one, where the dominant source is varied rather than re-derived.
        for (var i = 0; i < 20; i++)
            await store.StoreAsync(Entry($"s{i}", MemoryType.Insight, "claude-session", "Session", i, 0.90f));
        for (var i = 0; i < 10; i++)
            await store.StoreAsync(Entry($"p{i}", MemoryType.Procedure, "claude-session", "Proc", 200 + i, 0.90f));
        for (var i = 0; i < 10; i++)
            await store.StoreAsync(Entry($"h{i}", MemoryType.Heuristic, "claude-session", "Heur", 300 + i, 0.90f));

        var context = await new MemoryService(store).GetContextAsync(Repo, maxTokens: 4000);

        Assert.Equal(13, Count(context, "Session"));
        Assert.Equal(20, Items(context));
    }
}
