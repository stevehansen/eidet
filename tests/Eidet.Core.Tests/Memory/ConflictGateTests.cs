using Eidet.Core.Domain;
using Eidet.Core.Memory;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Unit tests for the write-time contradiction rule. A conflict requires ALL THREE of: near-duplicate
/// content (neighbor membership), opposite hard valence signs, and a high-trust incumbent.
/// </summary>
public class ConflictGateTests
{
    private static MemoryEntry Entry(
        Valence valence, MemoryProvenance provenance = MemoryProvenance.AgentInferred,
        MemoryType type = MemoryType.Insight, int echo = 0, int fizzle = 0, string content = "RavenDB embedded is the default storage mode") => new()
    {
        Id = $"memories/r/{type}/{Guid.NewGuid():N}",
        RepoId = "r",
        Type = type,
        Valence = valence,
        Provenance = provenance,
        Content = content,
        EchoCount = echo,
        FizzleCount = fizzle,
        IsLatest = true,
    };

    [Fact]
    public void HighTrustOppositeStance_Conflicts()
    {
        var incoming = Entry(Valence.Refuting);                       // "never use it"
        var incumbent = Entry(Valence.Affirming);                     // Insight/AgentInferred → trust 1.0

        var finding = ConflictGate.Check(incoming, [incumbent]);

        Assert.NotNull(finding);
        Assert.Equal(incumbent.Id, finding!.Value.ContradictedId);
        Assert.Equal(Valence.Refuting, finding.Value.Stance);
        Assert.Equal(Valence.Affirming, finding.Value.ContradictedStance);
        Assert.True(finding.Value.ContradictedTrust >= 0.9);
        Assert.True(finding.Value.Similarity > 0f);
    }

    [Fact]
    public void NeutralIncoming_NeverConflicts()
    {
        var incoming = Entry(Valence.Neutral);
        var incumbent = Entry(Valence.Affirming);

        Assert.Null(ConflictGate.Check(incoming, [incumbent]));
    }

    [Fact]
    public void CautionaryIncoming_NeverConflicts()
    {
        // Cautionary has sign 0 (soft stance) — bounds false positives to explicit opposite pairs.
        var incoming = Entry(Valence.Cautionary);
        var incumbent = Entry(Valence.Affirming);

        Assert.Null(ConflictGate.Check(incoming, [incumbent]));
    }

    [Fact]
    public void LowTrustIncumbent_DoesNotConflict()
    {
        var incoming = Entry(Valence.Refuting);
        // Pack provenance → trust floor 0.5, below the 0.9 high-trust bar.
        var incumbent = Entry(Valence.Affirming, provenance: MemoryProvenance.Pack);

        Assert.Null(ConflictGate.Check(incoming, [incumbent]));
    }

    [Fact]
    public void SameStance_DoesNotConflict()
    {
        var incoming = Entry(Valence.Affirming);
        var incumbent = Entry(Valence.Affirming);

        Assert.Null(ConflictGate.Check(incoming, [incumbent]));
    }

    [Fact]
    public void EmptyNeighbors_NoConflict()
    {
        Assert.Null(ConflictGate.Check(Entry(Valence.Refuting), []));
    }

    [Fact]
    public void PicksMostTrustedContradictingIncumbent()
    {
        var incoming = Entry(Valence.Refuting);
        var lowTrust = Entry(Valence.Affirming, provenance: MemoryProvenance.Pack, echo: 1); // still < 0.9
        var highTrust = Entry(Valence.Affirming);                                            // 1.0
        highTrust.Content = incoming.Content; // ensure it's the same near-dup content

        var finding = ConflictGate.Check(incoming, [lowTrust, highTrust]);

        Assert.NotNull(finding);
        Assert.Equal(highTrust.Id, finding!.Value.ContradictedId);
    }
}
