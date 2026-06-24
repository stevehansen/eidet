using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// The L1 wake-up hard-caps procedures at 3 (issue #35) — a wrongly-recalled procedure is
/// net-negative, so procedure pollution in the &lt;600-token wake-up is bounded regardless of the
/// soft 30% type budget. Seeds many high-importance Procedures (which would otherwise dominate the
/// top-K) and asserts at most three <c>[P]</c> lines come back from
/// <see cref="MemoryService.GetContextAsync"/>.
/// </summary>
public class ContextProcedureCapTests
{
    private static MemoryEntry Entry(string id, MemoryType type, float importance) => new()
    {
        Id = $"memories/repo-a/{type.ToString().ToLowerInvariant()}/{id}",
        RepoId = "repo-a",
        Type = type,
        Content = $"{type} memory {id}",
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
        Importance = importance,
    };

    [Fact]
    public async Task GetContext_caps_procedures_at_three_even_when_many_are_top_scored()
    {
        var store = new InMemoryEidetStore();
        // 10 procedures, all higher importance than the knowledge entries — without the hard cap
        // they would crowd out the wake-up. A handful of insights/heuristics fill the rest.
        for (var i = 0; i < 10; i++)
            await store.StoreAsync(Entry($"p{i}", MemoryType.Procedure, importance: 0.95f));
        for (var i = 0; i < 5; i++)
            await store.StoreAsync(Entry($"i{i}", MemoryType.Insight, importance: 0.5f));
        for (var i = 0; i < 5; i++)
            await store.StoreAsync(Entry($"h{i}", MemoryType.Heuristic, importance: 0.5f));

        // Generous token budget so truncation can't be what limits the procedure count.
        var context = await new MemoryService(store).GetContextAsync("repo-a", maxTokens: 4000);

        var procedureLines = context
            .Split('\n')
            .Count(l => l.TrimStart().StartsWith("[P]"));

        Assert.True(procedureLines <= 3, $"wake-up should cap procedures at 3, got {procedureLines}:\n{context}");
    }

    [Fact]
    public async Task GetContext_freed_procedure_slots_backfill_insights_not_heuristics()
    {
        var store = new InMemoryEidetStore();
        // Plenty of every type at equal high importance so the per-type budgets — not scarcity or
        // token truncation — decide the wake-up mix.
        for (var i = 0; i < 15; i++)
            await store.StoreAsync(Entry($"i{i}", MemoryType.Insight, importance: 0.9f));
        for (var i = 0; i < 10; i++)
            await store.StoreAsync(Entry($"p{i}", MemoryType.Procedure, importance: 0.9f));
        for (var i = 0; i < 10; i++)
            await store.StoreAsync(Entry($"h{i}", MemoryType.Heuristic, importance: 0.9f));

        var context = await new MemoryService(store).GetContextAsync("repo-a", maxTokens: 4000);
        var lines = context.Split('\n');
        var insights = lines.Count(l => l.TrimStart().StartsWith("[I]"));
        var procedures = lines.Count(l => l.TrimStart().StartsWith("[P]"));
        var heuristics = lines.Count(l => l.TrimStart().StartsWith("[H]"));

        Assert.Equal(3, procedures); // hard cap
        // The cap must NOT inflate heuristics (also action-shaped / net-negative-if-wrong); they keep
        // their uncapped 20% share. The 3 freed slots flow to fully-trusted insights (10 -> 13).
        Assert.True(heuristics <= 4, $"heuristics must keep their uncapped share (<=4), got {heuristics}:\n{context}");
        Assert.Equal(13, insights);
        Assert.Equal(20, insights + procedures + heuristics); // total item count unchanged
    }
}
