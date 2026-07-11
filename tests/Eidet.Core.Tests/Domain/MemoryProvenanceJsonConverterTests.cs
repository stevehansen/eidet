using Eidet.Core.Domain;
using Newtonsoft.Json;

namespace Eidet.Core.Tests.Domain;

public class MemoryProvenanceJsonConverterTests
{
    [Fact]
    public void Deserialize_Pack_ReturnsPack()
    {
        var result = JsonConvert.DeserializeObject<MemoryProvenance>("\"Pack\"");
        Assert.Equal(MemoryProvenance.Pack, result);
    }

    [Fact]
    public void Deserialize_LegacyBundle_ReturnsPack()
    {
        var result = JsonConvert.DeserializeObject<MemoryProvenance>("\"Bundle\"");
        Assert.Equal(MemoryProvenance.Pack, result);
    }

    [Fact]
    public void Deserialize_LegacyBundleLowercase_ReturnsPack()
    {
        var result = JsonConvert.DeserializeObject<MemoryProvenance>("\"bundle\"");
        Assert.Equal(MemoryProvenance.Pack, result);
    }

    [Fact]
    public void Serialize_Pack_WritesPack()
    {
        var json = JsonConvert.SerializeObject(MemoryProvenance.Pack);
        Assert.Equal("\"Pack\"", json);
    }

    [Fact]
    public void Deserialize_Reflection_ReturnsReflection()
    {
        var result = JsonConvert.DeserializeObject<MemoryProvenance>("\"Reflection\"");
        Assert.Equal(MemoryProvenance.Reflection, result);
    }

    [Fact]
    public void Serialize_Reflection_WritesReflection()
    {
        var json = JsonConvert.SerializeObject(MemoryProvenance.Reflection);
        Assert.Equal("\"Reflection\"", json);
    }

    [Fact]
    public void Roundtrip_PreservesAllValues()
    {
        foreach (var value in System.Enum.GetValues<MemoryProvenance>())
        {
            var json = JsonConvert.SerializeObject(value);
            var back = JsonConvert.DeserializeObject<MemoryProvenance>(json);
            Assert.Equal(value, back);
        }
    }
}
