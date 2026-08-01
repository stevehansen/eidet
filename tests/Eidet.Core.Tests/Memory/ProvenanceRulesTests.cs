using Eidet.Core.Domain;
using Eidet.Core.Memory;

namespace Eidet.Core.Tests.Memory;

/// <summary>
/// Anti-laundering derivation for syntheses (<see cref="ProvenanceRules"/>), extended in #80 to cover
/// <see cref="MemoryProvenance.Unknown"/>. The guarantee: a synthesis is born fully trusted
/// (<c>Consolidation</c>) only when EVERY contributor is trusted; one untrusted contributor demotes the
/// emission to the least-trusted contributor's provenance so recall keeps de-boosting it.
///
/// Unknown has to fail <see cref="ProvenanceRules.IsTrusted"/> for the same reason Pack does. Otherwise
/// consolidation becomes a laundry: feed it memories whose origin was never established and it hands back
/// a fully-trusted Insight, which is the exact "compression-amplified toxin" the rule exists to stop.
/// </summary>
public class ProvenanceRulesTests
{
    private static MemoryEntry Contributor(MemoryProvenance provenance) => new()
    {
        Id = $"memories/repo-a/observation/{provenance}",
        RepoId = "repo-a",
        Type = MemoryType.Observation,
        Content = $"a contributing observation with {provenance} provenance",
        Provenance = provenance,
    };

    [Theory]
    [InlineData(MemoryProvenance.UserStated, true)]
    [InlineData(MemoryProvenance.AgentInferred, true)]
    [InlineData(MemoryProvenance.ToolOutput, true)]
    [InlineData(MemoryProvenance.Consolidation, true)]
    [InlineData(MemoryProvenance.System, true)]
    [InlineData(MemoryProvenance.Intake, false)]
    [InlineData(MemoryProvenance.Pack, false)]
    [InlineData(MemoryProvenance.Reflection, false)]
    [InlineData(MemoryProvenance.Unknown, false)] // #80
    public void IsTrusted_classifies_every_provenance(MemoryProvenance provenance, bool expected)
    {
        Assert.Equal(expected, ProvenanceRules.IsTrusted(Contributor(provenance)));
    }

    [Fact]
    public void ForContributors_all_trusted_yields_Consolidation()
    {
        var provenance = ProvenanceRules.ForContributors(
        [
            Contributor(MemoryProvenance.AgentInferred),
            Contributor(MemoryProvenance.ToolOutput),
            Contributor(MemoryProvenance.UserStated),
        ]);

        Assert.Equal(MemoryProvenance.Consolidation, provenance);
    }

    [Fact]
    public void ForContributors_one_unknown_contributor_demotes_the_synthesis()
    {
        var provenance = ProvenanceRules.ForContributors(
        [
            Contributor(MemoryProvenance.AgentInferred),
            Contributor(MemoryProvenance.ToolOutput),
            Contributor(MemoryProvenance.Unknown),
        ]);

        Assert.NotEqual(MemoryProvenance.Consolidation, provenance);
        Assert.Equal(MemoryProvenance.Unknown, provenance);
        Assert.True(MemoryTrust.ProvenanceTrust(provenance) < 1.0);
    }

    [Fact]
    public void ForContributors_unknown_and_pack_are_equally_untrusted_so_either_may_be_inherited()
    {
        // Both sit on the import floor, and the rule takes the FIRST after ordering by trust — an
        // implementation detail among ties. What must hold is that the emission is demoted at all.
        var provenance = ProvenanceRules.ForContributors(
        [
            Contributor(MemoryProvenance.AgentInferred),
            Contributor(MemoryProvenance.Unknown),
            Contributor(MemoryProvenance.Pack),
        ]);

        Assert.Contains(provenance, new[] { MemoryProvenance.Unknown, MemoryProvenance.Pack });
        Assert.Equal(0.5, MemoryTrust.ProvenanceTrust(provenance));
    }
}
