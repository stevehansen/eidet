using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Behavioral tests for trust gating in the recall pipeline (issue #34). At recall time
/// <see cref="MemoryService.RecallInternalAsync"/> multiplies each candidate's fused score by
/// <see cref="MemoryTrust.Factor"/> and stamps <see cref="MemorySearchResult.TrustFactor"/>. The
/// contract: a provisional (Pack/Intake, or fresh Procedure/Heuristic) memory ranks BELOW a
/// fully-trusted one of EQUAL relevance, and earned echoes close the gap.
///
/// Driven through the scripted-arm <see cref="InMemoryScoredStore"/> (from RecallFusionTests) so
/// per-arm raw scores are identical for the two candidates — only the trust multiplier can move
/// the ranking. <see cref="MemoryService.ExplainRecallAsync"/> exposes the same per-row Trust.
/// </summary>
public class RecallTrustGatingTests
{
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    private static MemoryEntry Entry(
        string id,
        MemoryType type = MemoryType.Insight,
        MemoryProvenance provenance = MemoryProvenance.AgentInferred,
        int echo = 0,
        int fizzle = 0) => new()
    {
        Id = id,
        RepoId = "repo-a",
        Type = type,
        Provenance = provenance,
        Content = id,
        CreatedAt = Now,
        Validity = new Validity { ValidFrom = Now },
        EchoCount = echo,
        FizzleCount = fizzle,
        IsLatest = true,
        Importance = 0.5f,
    };

    [Fact]
    public async Task PackMemory_ranks_below_equally_relevant_trusted_memory()
    {
        var store = new InMemoryScoredStore();
        // Identical raw scores in BOTH arms — relevance is a tie; only trust can break it.
        store.SetArm(SearchArm.Lexical, ("trusted", 7.0), ("packed", 7.0));
        store.SetArm(SearchArm.Vector, ("trusted", 7.0), ("packed", 7.0));
        store.Seed(
            Entry("trusted", provenance: MemoryProvenance.AgentInferred),
            Entry("packed", provenance: MemoryProvenance.Pack));

        var svc = new MemoryService(store);
        var results = await svc.RecallAsync("repo-a", "query");

        Assert.Equal(2, results.Count);
        Assert.Equal("trusted", results[0].Id);
        Assert.Equal("packed", results[1].Id);
        Assert.True(results[0].Score > results[1].Score,
            $"trusted score ({results[0].Score}) should exceed packed score ({results[1].Score})");
    }

    [Fact]
    public async Task FreshProcedure_ranks_below_equally_relevant_insight()
    {
        var store = new InMemoryScoredStore();
        store.SetArm(SearchArm.Lexical, ("insight", 7.0), ("procedure", 7.0));
        store.SetArm(SearchArm.Vector, ("insight", 7.0), ("procedure", 7.0));
        store.Seed(
            Entry("insight", type: MemoryType.Insight),
            Entry("procedure", type: MemoryType.Procedure));

        var svc = new MemoryService(store);
        var results = await svc.RecallAsync("repo-a", "query");

        Assert.Equal("insight", results[0].Id);
        Assert.Equal("procedure", results[1].Id);
    }

    [Fact]
    public async Task TrustFactor_is_populated_below_one_for_lowTrust_and_one_for_trusted()
    {
        var store = new InMemoryScoredStore();
        store.SetArm(SearchArm.Lexical, ("trusted", 7.0), ("packed", 7.0), ("procedure", 7.0));
        store.SetArm(SearchArm.Vector, ("trusted", 7.0), ("packed", 7.0), ("procedure", 7.0));
        store.Seed(
            Entry("trusted", provenance: MemoryProvenance.AgentInferred),
            Entry("packed", provenance: MemoryProvenance.Pack),
            Entry("procedure", type: MemoryType.Procedure));

        var svc = new MemoryService(store);
        var results = await svc.RecallAsync("repo-a", "query");

        var trusted = results.Single(r => r.Id == "trusted");
        var packed = results.Single(r => r.Id == "packed");
        var procedure = results.Single(r => r.Id == "procedure");

        Assert.Equal(1.0f, trusted.TrustFactor);
        Assert.Equal(0.5f, packed.TrustFactor, precision: 5);       // Pack floor, no feedback
        Assert.Equal(0.7f, procedure.TrustFactor, precision: 5);    // Procedure floor, no feedback
    }

    [Fact]
    public async Task Enough_echoes_close_the_trust_gap_for_a_pack_memory()
    {
        // A fresh Pack memory is gated at the 0.5 floor; after many echoes its trust factor lifts
        // toward 1.0, closing the gap with a trusted memory. We assert on TrustFactor directly —
        // the trust gate is the contract under test. (The fused SCORE also carries the unrelated
        // UCB exploration term, which independently favors the low-feedback memory, so a raw score
        // comparison would not isolate the trust effect.)
        var fresh = new InMemoryScoredStore();
        fresh.SetArm(SearchArm.Lexical, ("packed", 7.0));
        fresh.SetArm(SearchArm.Vector, ("packed", 7.0));
        fresh.Seed(Entry("packed", provenance: MemoryProvenance.Pack, echo: 0));

        var echoed = new InMemoryScoredStore();
        echoed.SetArm(SearchArm.Lexical, ("packed", 7.0));
        echoed.SetArm(SearchArm.Vector, ("packed", 7.0));
        echoed.Seed(Entry("packed", provenance: MemoryProvenance.Pack, echo: 500));

        var freshTrust = (await new MemoryService(fresh).RecallAsync("repo-a", "query")).Single().TrustFactor;
        var echoedTrust = (await new MemoryService(echoed).RecallAsync("repo-a", "query")).Single().TrustFactor;

        Assert.Equal(0.5f, freshTrust, precision: 5);              // gated at the floor when unproven
        Assert.True(echoedTrust > freshTrust, $"echoed trust ({echoedTrust}) should exceed fresh ({freshTrust})");
        Assert.True(echoedTrust > 0.99f, $"echoed pack trust ({echoedTrust}) should approach full trust");
    }

    [Fact]
    public async Task ExplainRecall_emits_per_row_trust_and_gated_score()
    {
        var store = new InMemoryScoredStore();
        store.SetArm(SearchArm.Lexical, ("trusted", 7.0), ("packed", 7.0));
        store.SetArm(SearchArm.Vector, ("trusted", 7.0), ("packed", 7.0));
        store.Seed(
            Entry("trusted", provenance: MemoryProvenance.AgentInferred),
            Entry("packed", provenance: MemoryProvenance.Pack));

        var svc = new MemoryService(store);
        var explanation = await svc.ExplainRecallAsync("repo-a", new RecallOptions("query"));

        var trustedRow = explanation.Rows.Single(r => r.Id == "trusted");
        var packedRow = explanation.Rows.Single(r => r.Id == "packed");

        Assert.Equal(1.0, trustedRow.Trust);
        Assert.Equal(0.5, packedRow.Trust, precision: 12);
        // Gated == Fused · Trust — the production-recall score the diagnostic mirrors.
        Assert.Equal(packedRow.Fused * packedRow.Trust, packedRow.Gated, precision: 12);
        Assert.True(packedRow.Gated < trustedRow.Gated);
    }
}
