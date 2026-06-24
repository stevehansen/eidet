using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Tests for the realized-benefit ROI gate (issue #35) — the demotion lever ORTHOGONAL to
/// <see cref="MemoryTrust"/>. <see cref="MemoryRoi.Factor"/> is pure math: 1.0 everywhere except a
/// proven net-negative Procedure/Heuristic (<c>FizzleCount &gt; EchoCount</c>), where it is
/// <c>(echo + 3)/(fizzle + 3)</c>. The recall-time half mirrors <see cref="RecallTrustGatingTests"/>:
/// driven through the scripted-arm <see cref="InMemoryScoredStore"/> so identical raw scores isolate
/// the ROI multiplier, asserting <see cref="MemorySearchResult.RoiFactor"/> and the gated Score.
/// </summary>
public class RoiGatingTests
{
    private static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    private static MemoryEntry Entry(
        string id,
        MemoryType type = MemoryType.Procedure,
        int echo = 0,
        int fizzle = 0) => new()
    {
        Id = id,
        RepoId = "repo-a",
        Type = type,
        Content = id,
        CreatedAt = Now,
        Validity = new Validity { ValidFrom = Now },
        EchoCount = echo,
        FizzleCount = fizzle,
        IsLatest = true,
        Importance = 0.5f,
    };

    // ════════════════════════════════════════════════════════════════════════
    // Pure MemoryRoi.Factor — only action-shaped, net-negative memories are penalized
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(MemoryType.Insight)]
    [InlineData(MemoryType.Observation)]
    public void Knowledge_types_are_never_penalized_even_with_many_fizzles(MemoryType type)
    {
        Assert.Equal(1.0, MemoryRoi.Factor(Entry("x", type, echo: 0, fizzle: 50)));
    }

    [Theory]
    [InlineData(MemoryType.Procedure)]
    [InlineData(MemoryType.Heuristic)]
    public void ActionTypes_with_no_feedback_are_neutral(MemoryType type)
    {
        Assert.Equal(1.0, MemoryRoi.Factor(Entry("x", type, echo: 0, fizzle: 0)));
    }

    [Theory]
    [InlineData(MemoryType.Procedure)]
    [InlineData(MemoryType.Heuristic)]
    public void ActionTypes_at_parity_are_neutral(MemoryType type)
    {
        // echo == fizzle is NOT net-negative (FizzleCount <= EchoCount) → no penalty.
        Assert.Equal(1.0, MemoryRoi.Factor(Entry("x", type, echo: 5, fizzle: 5)));
    }

    [Theory]
    [InlineData(MemoryType.Procedure)]
    [InlineData(MemoryType.Heuristic)]
    public void ActionTypes_net_positive_are_neutral(MemoryType type)
    {
        // echo > fizzle → the positive side is handled by UCB/trust, not ROI.
        Assert.Equal(1.0, MemoryRoi.Factor(Entry("x", type, echo: 10, fizzle: 3)));
    }

    /// <summary>
    /// Exact penalty values for a net-negative Procedure: <c>(echo + 3)/(fizzle + 3)</c>.
    ///   0e/1f → 6/... wait: (0+3)/(1+3) = 0.75
    ///   0e/3f → 3/6   = 0.5
    ///   0e/5f → 3/8   = 0.375
    ///   2e/5f → 5/8   = 0.625  (echoes lift the factor back toward 1.0)
    /// </summary>
    [Theory]
    [InlineData(0, 1, 0.75)]
    [InlineData(0, 3, 0.5)]
    [InlineData(0, 5, 0.375)]
    [InlineData(2, 5, 0.625)]
    public void NetNegative_procedure_uses_exact_smoothed_ratio(int echo, int fizzle, double expected)
    {
        Assert.Equal(expected, MemoryRoi.Factor(Entry("x", MemoryType.Procedure, echo, fizzle)), precision: 12);
    }

    [Fact]
    public void NetNegative_heuristic_uses_the_same_ratio_as_procedure()
    {
        var proc = MemoryRoi.Factor(Entry("p", MemoryType.Procedure, echo: 1, fizzle: 4));
        var heur = MemoryRoi.Factor(Entry("h", MemoryType.Heuristic, echo: 1, fizzle: 4));
        Assert.Equal(proc, heur, precision: 12);
        Assert.Equal((1 + 3.0) / (4 + 3.0), heur, precision: 12);
    }

    [Fact]
    public void Factor_decreases_monotonically_as_fizzles_grow_and_stays_in_open_unit_interval()
    {
        var prev = MemoryRoi.Factor(Entry("x", MemoryType.Procedure, echo: 0, fizzle: 1));
        Assert.True(prev is > 0.0 and <= 1.0);
        for (var fizzle = 2; fizzle <= 200; fizzle++)
        {
            var roi = MemoryRoi.Factor(Entry("x", MemoryType.Procedure, echo: 0, fizzle));
            Assert.True(roi < prev, $"roi at fizzle={fizzle} ({roi}) should be below fizzle={fizzle - 1} ({prev})");
            Assert.True(roi is > 0.0 and <= 1.0, $"roi {roi} must be in (0, 1]");
            prev = roi;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // FizzleReasons.IsContentInvalidating — the steeper-penalty predicate
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(FizzleReason.VersionDrift)]
    [InlineData(FizzleReason.Incorrect)]
    public void ContentInvalidating_reasons_are_true(FizzleReason reason)
    {
        Assert.True(FizzleReasons.IsContentInvalidating(reason));
    }

    [Theory]
    [InlineData(FizzleReason.WrongContext)]
    [InlineData(FizzleReason.Other)]
    public void NonContentInvalidating_reasons_are_false(FizzleReason reason)
    {
        Assert.False(FizzleReasons.IsContentInvalidating(reason));
    }

    [Fact]
    public void Null_reason_is_not_content_invalidating()
    {
        Assert.False(FizzleReasons.IsContentInvalidating(null));
    }

    // ════════════════════════════════════════════════════════════════════════
    // Recall-time: ROI de-boosts a proven net-negative Procedure below an unfizzled clone
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Two Procedures with identical raw arm scores: one net-negative (fizzles &gt; echoes), one clean.
    /// The net-negative one gets <c>RoiFactor &lt; 1.0</c> and a strictly lower final Score; the clean
    /// one gets <c>RoiFactor == 1.0</c>. Trust is equal (both fresh Procedures), so ROI alone moves it.
    /// </summary>
    [Fact]
    public async Task NetNegative_procedure_scores_below_clean_clone_via_roi()
    {
        var store = new InMemoryScoredStore();
        store.SetArm(SearchArm.Lexical, ("clean", 7.0), ("badproc", 7.0));
        store.SetArm(SearchArm.Vector, ("clean", 7.0), ("badproc", 7.0));
        store.Seed(
            Entry("clean", MemoryType.Procedure, echo: 0, fizzle: 0),
            Entry("badproc", MemoryType.Procedure, echo: 0, fizzle: 5));

        var results = await new MemoryService(store).RecallAsync("repo-a", "query");

        var clean = results.Single(r => r.Id == "clean");
        var bad = results.Single(r => r.Id == "badproc");

        Assert.Equal(1.0f, clean.RoiFactor);
        Assert.True(bad.RoiFactor < 1.0f, $"net-negative proc RoiFactor ({bad.RoiFactor}) should be < 1.0");
        Assert.Equal(0.375f, bad.RoiFactor, precision: 5); // (0+3)/(5+3)
        Assert.True(bad.Score < clean.Score, $"bad score ({bad.Score}) should be below clean ({clean.Score})");
    }

    /// <summary>
    /// An Insight is never ROI-penalized: even with heavy fizzles its <see cref="MemorySearchResult.RoiFactor"/>
    /// stays 1.0 (the gate only fires for action-shaped types).
    /// </summary>
    [Fact]
    public async Task Insight_keeps_roi_factor_of_one_regardless_of_fizzles()
    {
        var store = new InMemoryScoredStore();
        store.SetArm(SearchArm.Lexical, ("ins", 7.0));
        store.SetArm(SearchArm.Vector, ("ins", 7.0));
        store.Seed(Entry("ins", MemoryType.Insight, echo: 0, fizzle: 20));

        var result = (await new MemoryService(store).RecallAsync("repo-a", "query")).Single();

        Assert.Equal(1.0f, result.RoiFactor);
    }

    /// <summary>A net-NONNEGATIVE Procedure (echoes ≥ fizzles) carries RoiFactor 1.0 at recall.</summary>
    [Fact]
    public async Task NetNonnegative_procedure_keeps_roi_factor_of_one()
    {
        var store = new InMemoryScoredStore();
        store.SetArm(SearchArm.Lexical, ("proc", 7.0));
        store.SetArm(SearchArm.Vector, ("proc", 7.0));
        store.Seed(Entry("proc", MemoryType.Procedure, echo: 4, fizzle: 4));

        var result = (await new MemoryService(store).RecallAsync("repo-a", "query")).Single();

        Assert.Equal(1.0f, result.RoiFactor);
    }
}
