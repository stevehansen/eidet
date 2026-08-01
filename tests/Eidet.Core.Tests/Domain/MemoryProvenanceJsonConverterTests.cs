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

    // ─── Closed-world on the way in (#80, STRIDE T-20) ──────────────────
    //
    // Anything this build cannot map to a defined value lands on Unknown, never on a trusted origin.
    // Before #80 every one of these cases deserialized to AgentInferred — full recall trust — so a
    // malformed or hand-written database row minted the trust it should have failed to establish.

    [Fact]
    public void Deserialize_EmptyString_ReturnsUnknown()
    {
        Assert.Equal(MemoryProvenance.Unknown, JsonConvert.DeserializeObject<MemoryProvenance>("\"\""));
    }

    [Fact]
    public void Deserialize_UnparseableName_ReturnsUnknown()
    {
        Assert.Equal(
            MemoryProvenance.Unknown,
            JsonConvert.DeserializeObject<MemoryProvenance>("\"TotallyMadeUpOrigin\""));
    }

    [Fact]
    public void Deserialize_OutOfRangeInteger_ReturnsUnknown()
    {
        // A direct database write (or a future build's value read by an older one) can store an ordinal
        // this enum does not define. Enum.IsDefined rejects it rather than casting it through.
        Assert.Equal(MemoryProvenance.Unknown, JsonConvert.DeserializeObject<MemoryProvenance>("99"));
    }

    [Fact]
    public void Deserialize_InRangeInteger_StillRoundTrips()
    {
        // Legacy documents stored provenance as an ordinal; the closed-world guard must not break them.
        Assert.Equal(MemoryProvenance.Pack, JsonConvert.DeserializeObject<MemoryProvenance>("5"));
        Assert.Equal(MemoryProvenance.UserStated, JsonConvert.DeserializeObject<MemoryProvenance>("0"));
        Assert.Equal(MemoryProvenance.Unknown, JsonConvert.DeserializeObject<MemoryProvenance>("8"));
    }

    [Fact]
    public void Deserialize_MissingProperty_LeavesEntryProvenanceUnknown()
    {
        // The pre-field document case: a memory persisted before Provenance existed. The property
        // initializer on MemoryEntry (not the converter) supplies Unknown, and the two have to agree —
        // an absent value and an unreadable one are the same failure to establish provenance.
        var entry = JsonConvert.DeserializeObject<MemoryEntry>(
            """{ "Id": "memories/repo/insight/abc123abc123", "RepoId": "repo", "Content": "a pre-provenance memory" }""");

        Assert.NotNull(entry);
        Assert.Equal(MemoryProvenance.Unknown, entry!.Provenance);
    }

    [Fact]
    public void Deserialize_EntryWithLegacyBundleAlias_StillLandsOnPack()
    {
        var entry = JsonConvert.DeserializeObject<MemoryEntry>(
            """{ "Id": "memories/repo/insight/abc123abc123", "RepoId": "repo", "Provenance": "Bundle" }""");

        Assert.Equal(MemoryProvenance.Pack, entry!.Provenance);
    }
}
