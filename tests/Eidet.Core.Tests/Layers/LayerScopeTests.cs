using Eidet.Core.Domain;
using Eidet.Core.Layers;

namespace Eidet.Core.Tests.Layers;

public class LayerScopeTests
{
    // ─── Local factory ────────────────────────────────────────────

    [Fact]
    public void Local_SingleRepoNoLayers()
    {
        var scope = LayerScope.Local("P--Eidet");

        Assert.Equal("P--Eidet", scope.PrimaryRepoId);
        Assert.Equal(["P--Eidet"], scope.RepoIds);
        Assert.Empty(scope.MountedLayers);
        Assert.False(scope.CrossRepo);
    }

    // ─── IsLocal predicate ────────────────────────────────────────

    [Fact]
    public void IsLocal_SameRepoNoLayer_ReturnsTrue()
    {
        var scope = LayerScope.Local("P--Eidet");
        var entry = new MemoryEntry { RepoId = "P--Eidet", LayerId = null };
        Assert.True(scope.IsLocal(entry));
    }

    [Fact]
    public void IsLocal_DifferentRepo_ReturnsFalse()
    {
        var scope = LayerScope.Local("P--Eidet");
        var entry = new MemoryEntry { RepoId = "P--OtherProject", LayerId = null };
        Assert.False(scope.IsLocal(entry));
    }

    [Fact]
    public void IsLocal_HasLayerId_ReturnsFalse()
    {
        var scope = LayerScope.Local("P--Eidet");
        var entry = new MemoryEntry { RepoId = "P--Eidet", LayerId = "pack:acme-v1" };
        Assert.False(scope.IsLocal(entry));
    }

    [Fact]
    public void IsLocal_RepoMatchCaseInsensitive()
    {
        var scope = LayerScope.Local("P--Eidet");
        var entry = new MemoryEntry { RepoId = "p--eidet", LayerId = null };
        Assert.True(scope.IsLocal(entry));
    }

    // ─── IsLocalRepo (cheap variant) ──────────────────────────────

    [Fact]
    public void IsLocalRepo_PrimaryRepo_ReturnsTrue()
    {
        var scope = LayerScope.Local("P--Eidet");
        Assert.True(scope.IsLocalRepo("P--Eidet"));
        Assert.True(scope.IsLocalRepo("p--eidet"));
    }

    [Fact]
    public void IsLocalRepo_OtherRepo_ReturnsFalse()
    {
        var scope = LayerScope.Local("P--Eidet");
        Assert.False(scope.IsLocalRepo("P--OtherProject"));
    }

    // ─── De-boost constant ────────────────────────────────────────

    [Fact]
    public void NonLocalDeBoost_Is080()
    {
        Assert.Equal(0.8f, LayerScope.NonLocalDeBoost);
    }

    // ─── Cross-repo with layers ───────────────────────────────────

    [Fact]
    public void CrossRepoScope_RetainsRepoIdsAndLayers()
    {
        var layers = new[] { new MemoryLayer { Id = "pack:dotnet-best", Type = LayerType.Base } };
        var scope = new LayerScope(
            PrimaryRepoId: "P--Eidet",
            RepoIds: ["P--Eidet", "P--Other"],
            MountedLayers: layers,
            CrossRepo: true);

        Assert.Equal(2, scope.RepoIds.Count);
        Assert.Single(scope.MountedLayers);
        Assert.True(scope.CrossRepo);
    }
}
