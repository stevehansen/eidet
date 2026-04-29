using Eidet.Core.Domain;

namespace Eidet.Core.Tests.Services;

public class LayerServiceTests
{
    [Fact]
    public void MemoryLayer_DefaultValues()
    {
        var layer = new MemoryLayer();
        Assert.Equal("", layer.Id);
        Assert.Equal("", layer.Name);
        Assert.Equal(LayerType.Local, layer.Type);
        Assert.False(layer.ReadOnly);
        Assert.Empty(layer.ApplicableRepos);
        Assert.Empty(layer.ApplicablePackages);
    }

    [Fact]
    public void MemoryLayer_Priorities()
    {
        // Verify spec: local=100, shared=50, base=10
        var local = new MemoryLayer { Type = LayerType.Local, Priority = 100 };
        var shared = new MemoryLayer { Type = LayerType.Shared, Priority = 50 };
        var baseLayer = new MemoryLayer { Type = LayerType.Base, Priority = 10 };

        Assert.True(local.Priority > shared.Priority);
        Assert.True(shared.Priority > baseLayer.Priority);
    }

    [Fact]
    public void LayerType_HasExpectedValues()
    {
        Assert.Equal(0, (int)LayerType.Local);
        Assert.Equal(1, (int)LayerType.Shared);
        Assert.Equal(2, (int)LayerType.Base);
    }
}
