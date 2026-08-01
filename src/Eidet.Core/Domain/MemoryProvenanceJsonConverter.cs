using Newtonsoft.Json;

namespace Eidet.Core.Domain;

/// Reads "Bundle" (legacy) as Pack; writes Pack as "Pack". Closed-world on the way in: anything this
/// build cannot map to a defined value — an empty string, an unrecognized name, an out-of-range integer
/// from a direct database write — deserializes to <see cref="MemoryProvenance.Unknown"/> rather than to
/// a trusted origin, so a malformed payload cannot mint full recall trust (#80, STRIDE T-20).
internal sealed class MemoryProvenanceJsonConverter : JsonConverter<MemoryProvenance>
{
    public override void WriteJson(JsonWriter writer, MemoryProvenance value, JsonSerializer serializer)
        => writer.WriteValue(value.ToString());

    public override MemoryProvenance ReadJson(JsonReader reader, System.Type objectType, MemoryProvenance existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Integer)
        {
            var ordinal = (MemoryProvenance)System.Convert.ToInt32(reader.Value);
            return System.Enum.IsDefined(ordinal) ? ordinal : MemoryProvenance.Unknown;
        }

        var raw = reader.Value?.ToString();
        if (string.IsNullOrEmpty(raw))
            return MemoryProvenance.Unknown;

        if (string.Equals(raw, "Bundle", System.StringComparison.OrdinalIgnoreCase))
            return MemoryProvenance.Pack;

        return System.Enum.TryParse<MemoryProvenance>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : MemoryProvenance.Unknown;
    }
}
