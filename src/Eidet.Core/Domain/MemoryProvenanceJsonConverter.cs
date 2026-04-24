using Newtonsoft.Json;

namespace Eidet.Core.Domain;

/// Reads "Bundle" (legacy) as Pack; writes Pack as "Pack".
internal sealed class MemoryProvenanceJsonConverter : JsonConverter<MemoryProvenance>
{
    public override void WriteJson(JsonWriter writer, MemoryProvenance value, JsonSerializer serializer)
        => writer.WriteValue(value.ToString());

    public override MemoryProvenance ReadJson(JsonReader reader, System.Type objectType, MemoryProvenance existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Integer)
            return (MemoryProvenance)System.Convert.ToInt32(reader.Value);

        var raw = reader.Value?.ToString();
        if (string.IsNullOrEmpty(raw))
            return MemoryProvenance.AgentInferred;

        if (string.Equals(raw, "Bundle", System.StringComparison.OrdinalIgnoreCase))
            return MemoryProvenance.Pack;

        return System.Enum.TryParse<MemoryProvenance>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : MemoryProvenance.AgentInferred;
    }
}
