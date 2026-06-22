using Eidet.Core.Domain;
using Eidet.Core.Memory;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Unit tests for <see cref="MemoryTrust"/> (issue #34) — the derived, never-stored anti-poisoning
/// trust factor. The contract: trust ∈ (0, 1.0], starting at the LOWER of the provenance floor
/// (Intake/Pack = 0.5, else 1.0) and the type floor (Procedure/Heuristic = 0.7, else 1.0), then
/// lifted toward 1.0 by earned echoes: <c>trust = floor + (1 - floor) · echo/(echo + fizzle + K)</c>
/// with K = 3.
///
/// Pure math, no clock, no store — feedback counts are the only input beyond provenance/type.
///
/// AS-BUILT floor combination is <c>Math.Min(provenanceFloor, typeFloor)</c> (MemoryTrust.cs:38),
/// which matches the original spec's <c>min(...)</c>. (The pipeline brief flagged a suspected
/// MULTIPLICATIVE combination — that is NOT what the code does; see PackProcedure_* below.)
/// </summary>
public class MemoryTrustTests
{
    private static MemoryEntry Entry(
        MemoryProvenance provenance = MemoryProvenance.AgentInferred,
        MemoryType type = MemoryType.Insight,
        int echo = 0,
        int fizzle = 0) => new()
    {
        Id = "memories/repo-a/insight/x",
        RepoId = "repo-a",
        Type = type,
        Provenance = provenance,
        Content = "x",
        EchoCount = echo,
        FizzleCount = fizzle,
    };

    // ─── Baseline: fully-trusted honest path is never penalized ───────────

    [Fact]
    public void Trusted_provenance_nonProcedural_type_zero_feedback_is_full_trust()
    {
        var trust = MemoryTrust.Factor(Entry(MemoryProvenance.AgentInferred, MemoryType.Insight));
        Assert.Equal(1.0, trust);
    }

    // ─── Provenance floor (type held neutral at Insight) ──────────────────

    [Theory]
    [InlineData(MemoryProvenance.Intake)]
    [InlineData(MemoryProvenance.Pack)]
    public void Import_provenance_floors_trust_at_half_with_zero_feedback(MemoryProvenance provenance)
    {
        var trust = MemoryTrust.Factor(Entry(provenance, MemoryType.Insight));
        Assert.Equal(0.5, trust);
    }

    [Theory]
    [InlineData(MemoryProvenance.AgentInferred)]
    [InlineData(MemoryProvenance.ToolOutput)]
    [InlineData(MemoryProvenance.UserStated)]
    [InlineData(MemoryProvenance.System)]
    [InlineData(MemoryProvenance.Consolidation)]
    public void FirstParty_provenance_is_fully_trusted_with_zero_feedback(MemoryProvenance provenance)
    {
        var trust = MemoryTrust.Factor(Entry(provenance, MemoryType.Insight));
        Assert.Equal(1.0, trust);
    }

    [Theory]
    [InlineData(MemoryProvenance.Intake, 0.5)]
    [InlineData(MemoryProvenance.Pack, 0.5)]
    [InlineData(MemoryProvenance.AgentInferred, 1.0)]
    [InlineData(MemoryProvenance.ToolOutput, 1.0)]
    [InlineData(MemoryProvenance.UserStated, 1.0)]
    [InlineData(MemoryProvenance.System, 1.0)]
    [InlineData(MemoryProvenance.Consolidation, 1.0)]
    public void ProvenanceTrust_returns_expected_floor(MemoryProvenance provenance, double expected)
    {
        Assert.Equal(expected, MemoryTrust.ProvenanceTrust(provenance));
    }

    // ─── Type floor (provenance held neutral at AgentInferred) ────────────

    [Theory]
    [InlineData(MemoryType.Procedure)]
    [InlineData(MemoryType.Heuristic)]
    public void ActionShaped_types_floor_trust_at_0_7_with_zero_feedback(MemoryType type)
    {
        var trust = MemoryTrust.Factor(Entry(MemoryProvenance.AgentInferred, type));
        Assert.Equal(0.7, trust);
    }

    [Theory]
    [InlineData(MemoryType.Insight)]
    [InlineData(MemoryType.Observation)]
    public void Knowledge_types_are_fully_trusted_with_zero_feedback(MemoryType type)
    {
        var trust = MemoryTrust.Factor(Entry(MemoryProvenance.AgentInferred, type));
        Assert.Equal(1.0, trust);
    }

    // ─── Floor combination rule: AS-BUILT is min(...), NOT the product ────

    /// <summary>
    /// Pins the floor-combination rule. A Pack Procedure combines provenance floor 0.5 and type
    /// floor 0.7. The AS-BUILT code uses <c>Math.Min</c> ⇒ 0.5. A multiplicative rule would give
    /// 0.5·0.7 = 0.35. This asserts the min result (0.5) and would fail loudly if the code were the
    /// product (0.35) — the combination rule the pipeline brief flagged as under review.
    /// </summary>
    [Fact]
    public void PackProcedure_floor_is_min_of_floors_not_product()
    {
        var trust = MemoryTrust.Factor(Entry(MemoryProvenance.Pack, MemoryType.Procedure, echo: 0, fizzle: 0));

        Assert.Equal(0.5, trust);                  // min(0.5, 0.7) == 0.5  (AS-BUILT)
        Assert.NotEqual(0.35, trust, precision: 6); // product 0.5 * 0.7 would be 0.35 (NOT the rule)
    }

    [Fact]
    public void IntakeHeuristic_floor_is_min_of_floors()
    {
        // min(provenance 0.5, type 0.7) == 0.5.
        var trust = MemoryTrust.Factor(Entry(MemoryProvenance.Intake, MemoryType.Heuristic));
        Assert.Equal(0.5, trust);
    }

    // ─── Echo-lift formula: exact pinned values ───────────────────────────

    /// <summary>
    /// Exact formula values at known (echo, fizzle) points for a Pack Insight (floor 0.5):
    ///   echo=3,fizzle=0 → 0.5 + 0.5·(3/6)   = 0.75
    ///   echo=3,fizzle=3 → 0.5 + 0.5·(3/9)   ≈ 0.6667  (fizzles dampen the same echo count)
    ///   echo=9,fizzle=0 → 0.5 + 0.5·(9/12)  = 0.875
    /// </summary>
    [Theory]
    [InlineData(3, 0, 0.75)]
    [InlineData(3, 3, 0.6666666666666666)]
    [InlineData(9, 0, 0.875)]
    [InlineData(0, 0, 0.5)]
    public void EchoLift_exact_formula_for_pack_insight(int echo, int fizzle, double expected)
    {
        var trust = MemoryTrust.Factor(Entry(MemoryProvenance.Pack, MemoryType.Insight, echo, fizzle));
        Assert.Equal(expected, trust, precision: 12);
    }

    /// <summary>
    /// Exact formula value for a Procedure (type floor 0.7, AgentInferred provenance):
    ///   echo=9,fizzle=0 → 0.7 + 0.3·(9/12) = 0.925.
    /// </summary>
    [Fact]
    public void EchoLift_exact_formula_for_procedure_floor()
    {
        var trust = MemoryTrust.Factor(Entry(MemoryProvenance.AgentInferred, MemoryType.Procedure, echo: 9, fizzle: 0));
        Assert.Equal(0.925, trust, precision: 12);
    }

    // ─── Monotonicity properties ──────────────────────────────────────────

    [Fact]
    public void EchoLift_raises_trust_monotonically_toward_one()
    {
        var prev = MemoryTrust.Factor(Entry(MemoryProvenance.Pack, MemoryType.Insight, echo: 0));
        for (var echo = 1; echo <= 50; echo++)
        {
            var trust = MemoryTrust.Factor(Entry(MemoryProvenance.Pack, MemoryType.Insight, echo));
            Assert.True(trust > prev, $"trust at echo={echo} ({trust}) should exceed echo={echo - 1} ({prev})");
            Assert.True(trust <= 1.0);
            prev = trust;
        }

        // Asymptotically approaches (never exceeds) 1.0 as echoes grow large.
        var huge = MemoryTrust.Factor(Entry(MemoryProvenance.Pack, MemoryType.Insight, echo: 100_000));
        Assert.True(huge > 0.99 && huge <= 1.0, $"trust at echo=100000 was {huge}");
    }

    [Fact]
    public void Fizzles_dampen_the_lift_more_fizzle_lower_trust_at_equal_echoes()
    {
        const int echo = 10;
        var noFizzle = MemoryTrust.Factor(Entry(MemoryProvenance.Pack, MemoryType.Insight, echo, fizzle: 0));
        var someFizzle = MemoryTrust.Factor(Entry(MemoryProvenance.Pack, MemoryType.Insight, echo, fizzle: 5));
        var moreFizzle = MemoryTrust.Factor(Entry(MemoryProvenance.Pack, MemoryType.Insight, echo, fizzle: 20));

        Assert.True(noFizzle > someFizzle, $"0 fizzle ({noFizzle}) should beat 5 fizzle ({someFizzle})");
        Assert.True(someFizzle > moreFizzle, $"5 fizzle ({someFizzle}) should beat 20 fizzle ({moreFizzle})");

        // Even with heavy fizzle the floor still holds — trust never drops below the floor.
        Assert.True(moreFizzle >= 0.5, $"trust ({moreFizzle}) must not fall below the floor 0.5");
    }

    // ─── Range + determinism ──────────────────────────────────────────────

    [Theory]
    [InlineData(MemoryProvenance.Pack, MemoryType.Procedure, 0, 0)]
    [InlineData(MemoryProvenance.Intake, MemoryType.Heuristic, 5, 100)]
    [InlineData(MemoryProvenance.AgentInferred, MemoryType.Insight, 0, 0)]
    [InlineData(MemoryProvenance.UserStated, MemoryType.Observation, 1000, 1000)]
    [InlineData(MemoryProvenance.Pack, MemoryType.Insight, 0, 9999)]
    public void Factor_is_always_in_open_unit_interval(MemoryProvenance prov, MemoryType type, int echo, int fizzle)
    {
        var trust = MemoryTrust.Factor(Entry(prov, type, echo, fizzle));
        Assert.True(trust > 0.0, $"trust {trust} must be > 0");
        Assert.True(trust <= 1.0, $"trust {trust} must be <= 1.0");
    }

    [Fact]
    public void Factor_is_deterministic_across_repeated_calls()
    {
        var entry = Entry(MemoryProvenance.Pack, MemoryType.Procedure, echo: 7, fizzle: 4);
        var first = MemoryTrust.Factor(entry);
        for (var i = 0; i < 5; i++)
            Assert.Equal(first, MemoryTrust.Factor(entry));
    }
}
