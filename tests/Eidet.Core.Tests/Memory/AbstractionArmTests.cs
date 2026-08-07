using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Tests for the ABSTRACTION arm — a third fusion arm scored against each memory's one-line
/// self-description rather than its whole text, so a query matching what a memory IS is not outvoted
/// by a long body in the composite embedding.
///
/// Section A — the fusion math: the arm rides on top of the lex/vec blend at weight Beta, an
/// abstraction-only hit still enters the candidate pool, and — the compatibility contract the
/// benchmark scorecard depends on — an ABSENT third arm leaves scores bit-identical to two-arm fusion
/// no matter what Beta is.
///
/// Section B — through RecallAsync: a memory only the abstraction arm returns is recallable, and a
/// store with no abstraction index (every fake, via the interface default) ranks exactly as before.
/// </summary>
public class AbstractionArmTests
{
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    private static MemoryEntry Entry(string id, string repoId = "repo-a") => new()
    {
        Id = id,
        RepoId = repoId,
        Type = MemoryType.Insight,
        Content = id,
        CreatedAt = Now,
        IsLatest = true,
        Validity = new Validity { ValidFrom = Now },
        Importance = 0.5f,
    };

    // ════════════════════════════════════════════════════════════════════════
    // Section A — fusion math
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A1 — THE compatibility contract. An empty abstraction arm normalizes to 0 for every candidate,
    /// so three-arm fusion with no third arm equals two-arm fusion exactly — for ANY Beta. This is what
    /// lets the benchmark scorecard keep ranking over two-arm pools without its numbers moving.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.35)]
    [InlineData(1.0)]
    public void Fuse_EmptyAbstractionArm_IdenticalToTwoArmFusion(double beta)
    {
        var a = Entry("a");
        var b = Entry("b");
        var lex = new List<ScoredHit> { new(a, 10.0), new(b, 5.0) };
        var vec = new List<ScoredHit> { new(b, 9.0), new(a, 3.0) };
        var weights = RecallWeights.Default with { Beta = beta };

        var twoArm = RecallScoring.Fuse(lex, vec, weights, Now);
        var threeArm = RecallScoring.Fuse(lex, vec, [], weights, Now);

        Assert.Equal(twoArm.Count, threeArm.Count);
        foreach (var (expected, actual) in twoArm.Zip(threeArm))
        {
            Assert.Equal(expected.Entry.Id, actual.Entry.Id);
            Assert.Equal(expected.Fused, actual.Fused, precision: 12);
        }
    }

    /// <summary>
    /// A2 — the arm rides ON TOP of the convex lex/vec blend: a candidate identical in both primary
    /// arms but stronger in the abstraction arm gains exactly Beta·(normAbs difference).
    /// </summary>
    [Fact]
    public void Fuse_AbstractionArm_AddsBetaTimesNormalizedScore()
    {
        var top = Entry("top");
        var bottom = Entry("bottom");
        // Both tie in lex and vec (all-equal arm ⇒ every candidate normalizes to 1.0) ...
        var lex = new List<ScoredHit> { new(top, 5.0), new(bottom, 5.0) };
        var vec = new List<ScoredHit> { new(top, 5.0), new(bottom, 5.0) };
        // ... and differ ONLY on the abstraction arm (normAbs 1.0 vs 0.0).
        var abs = new List<ScoredHit> { new(top, 9.0), new(bottom, 1.0) };

        var weights = RecallWeights.Default with { Beta = 0.35 };
        var fused = RecallScoring.Fuse(lex, vec, abs, weights, Now);

        var t = fused.Single(c => c.Entry.Id == "top");
        var bt = fused.Single(c => c.Entry.Id == "bottom");
        Assert.Equal(1.0, t.Abs, precision: 12);
        Assert.Equal(0.0, bt.Abs, precision: 12);
        Assert.Equal(0.35, t.Fused - bt.Fused, precision: 12);
        Assert.Equal("top", fused[0].Entry.Id);
    }

    /// <summary>
    /// A3 — a memory NO primary arm returned still enters the pool on the abstraction arm alone. That
    /// is the point of the arm: reaching a memory whose body diluted it out of the composite vector.
    /// </summary>
    [Fact]
    public void Fuse_AbstractionOnlyHit_EntersCandidatePool()
    {
        var inArms = Entry("inArms");
        var absOnly = Entry("absOnly");
        var lex = new List<ScoredHit> { new(inArms, 5.0) };
        var vec = new List<ScoredHit> { new(inArms, 5.0) };
        var abs = new List<ScoredHit> { new(absOnly, 9.0) };

        var fused = RecallScoring.Fuse(lex, vec, abs, RecallWeights.Default, Now);

        var only = Assert.Single(fused, c => c.Entry.Id == "absOnly");
        Assert.Equal(0.0, only.Lex);
        Assert.Equal(0.0, only.Vec);
        Assert.Equal(1.0, only.Abs, precision: 12); // single-candidate arm ⇒ normalizes to 1.0
    }

    /// <summary>A4 — Beta=0 mutes the arm entirely: an abstraction hit still joins the pool (it is a
    /// real candidate) but contributes no score of its own.</summary>
    [Fact]
    public void Fuse_BetaZero_ArmContributesNothing()
    {
        var a = Entry("a");
        var lex = new List<ScoredHit> { new(a, 5.0) };
        var abs = new List<ScoredHit> { new(a, 9.0) };

        var muted = RecallScoring.Fuse(lex, [], abs, RecallWeights.Default with { Beta = 0.0 }, Now);
        var noArm = RecallScoring.Fuse(lex, [], [], RecallWeights.Default with { Beta = 0.0 }, Now);

        Assert.Equal(noArm.Single().Fused, muted.Single().Fused, precision: 12);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Section B — through MemoryService.RecallAsync
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// B1 — end to end: a memory returned ONLY by the abstraction arm is recallable. Before the arm
    /// existed there was no path to it at all.
    /// </summary>
    [Fact]
    public async Task RecallAsync_AbstractionOnlyHit_IsReturned()
    {
        var store = new InMemoryScoredStore();
        store.Seed(Entry("hit"), Entry("absOnly"));
        store.SetArm(SearchArm.Lexical, ("hit", 7.0));
        store.SetArm(SearchArm.Vector, ("hit", 7.0));
        store.SetArm(SearchArm.Abstraction, ("absOnly", 9.0));

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        Assert.Contains(results, r => r.Id == "absOnly");
    }

    /// <summary>
    /// B2 — a store with no abstraction index (the <see cref="IEidetStore"/> default, which every fake
    /// inherits) is unaffected: the arm returns nothing and ranking is exactly the two-arm result.
    /// </summary>
    [Fact]
    public async Task RecallAsync_StoreWithoutAbstractionIndex_RanksAsBefore()
    {
        var store = new InMemoryScoredStore();
        store.Seed(Entry("a"), Entry("b"));
        store.SetArm(SearchArm.Lexical, ("a", 10.0), ("b", 5.0));
        store.SetArm(SearchArm.Vector, ("b", 9.0), ("a", 3.0));

        var results = await new MemoryService(store).RecallAsync("repo-a", new RecallOptions("q"));

        var explained = await new MemoryService(store).ExplainRecallAsync("repo-a", new RecallOptions("q"));
        Assert.All(explained.Rows, r => Assert.Equal(0.0, r.Abs));
        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// B3 — the abstraction arm is SEMANTIC evidence, so a hit carried by it alone must not teach the
    /// alpha learner that this repo is lexical. Its lexical share is 0, not the 0.5 no-arm prior.
    /// </summary>
    [Fact]
    public void Fuse_AbstractionOnlyHit_HasZeroLexicalShare()
    {
        var absOnly = Entry("absOnly");
        var fused = RecallScoring.Fuse([], [], [new ScoredHit(absOnly, 9.0)], RecallWeights.Default, Now);

        var c = fused.Single();
        var arms = c.Lex + c.Vec + c.Abs;
        Assert.True(arms > 0, "an abstraction-only hit must carry arm evidence, else it falls to the 0.5 prior");
        Assert.Equal(0.0, c.Lex / arms, precision: 12);
    }
}
