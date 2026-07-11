using Eidet.Core.Domain;
using Eidet.Core.Memory;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Unit truth-table for <see cref="ValencePolarity"/> — the single home for valence sign
/// arithmetic that the three write choke points ask <c>Conflicts</c>/<c>Merge</c>. Neutral and
/// Cautionary are deliberately sign-0 (a warning does not contradict an affirming claim), so only
/// the hard Affirming↔Refuting pair conflicts.
/// </summary>
public class ValencePolarityTests
{
    // ─── Sign ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Valence.Neutral, 0)]
    [InlineData(Valence.Affirming, 1)]
    [InlineData(Valence.Refuting, -1)]
    [InlineData(Valence.Cautionary, 0)]
    public void Sign_maps_each_valence(Valence v, int expected)
    {
        Assert.Equal(expected, ValencePolarity.Sign(v));
    }

    // ─── Conflicts ────────────────────────────────────────────────────

    [Theory]
    // Only the hard Affirming↔Refuting pair conflicts (both orders).
    [InlineData(Valence.Affirming, Valence.Refuting, true)]
    [InlineData(Valence.Refuting, Valence.Affirming, true)]
    // Same-sign hard pairs never conflict.
    [InlineData(Valence.Affirming, Valence.Affirming, false)]
    [InlineData(Valence.Refuting, Valence.Refuting, false)]
    // Anything touching Neutral is free to collapse.
    [InlineData(Valence.Neutral, Valence.Neutral, false)]
    [InlineData(Valence.Neutral, Valence.Affirming, false)]
    [InlineData(Valence.Affirming, Valence.Neutral, false)]
    [InlineData(Valence.Neutral, Valence.Refuting, false)]
    [InlineData(Valence.Refuting, Valence.Neutral, false)]
    // Cautionary is sign-0 — it warns but does not contradict, so it never conflicts.
    [InlineData(Valence.Cautionary, Valence.Cautionary, false)]
    [InlineData(Valence.Cautionary, Valence.Affirming, false)]
    [InlineData(Valence.Affirming, Valence.Cautionary, false)]
    [InlineData(Valence.Cautionary, Valence.Refuting, false)]
    [InlineData(Valence.Refuting, Valence.Cautionary, false)]
    [InlineData(Valence.Cautionary, Valence.Neutral, false)]
    [InlineData(Valence.Neutral, Valence.Cautionary, false)]
    public void Conflicts_true_only_for_opposite_hard_signs(Valence a, Valence b, bool expected)
    {
        Assert.Equal(expected, ValencePolarity.Conflicts(a, b));
    }

    // ─── Merge ────────────────────────────────────────────────────────

    [Fact]
    public void Merge_returns_the_non_neutral_operand()
    {
        Assert.Equal(Valence.Refuting, ValencePolarity.Merge(Valence.Neutral, Valence.Refuting));
        Assert.Equal(Valence.Refuting, ValencePolarity.Merge(Valence.Refuting, Valence.Neutral));
        Assert.Equal(Valence.Cautionary, ValencePolarity.Merge(Valence.Neutral, Valence.Cautionary));
        Assert.Equal(Valence.Affirming, ValencePolarity.Merge(Valence.Affirming, Valence.Neutral));
    }

    [Fact]
    public void Merge_keeps_a_when_both_operands_are_non_neutral()
    {
        // The survivor keeps its own opinionated stance (a) over the discarded one (b).
        Assert.Equal(Valence.Affirming, ValencePolarity.Merge(Valence.Affirming, Valence.Refuting));
        Assert.Equal(Valence.Refuting, ValencePolarity.Merge(Valence.Refuting, Valence.Cautionary));
        Assert.Equal(Valence.Cautionary, ValencePolarity.Merge(Valence.Cautionary, Valence.Affirming));
    }

    [Fact]
    public void Merge_returns_neutral_when_both_are_neutral()
    {
        Assert.Equal(Valence.Neutral, ValencePolarity.Merge(Valence.Neutral, Valence.Neutral));
    }
}
