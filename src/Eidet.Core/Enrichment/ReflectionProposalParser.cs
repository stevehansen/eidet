using System.Text.Json;
using Eidet.Core.Domain;

namespace Eidet.Core.Enrichment;

/// <summary>
/// Parses the strict-JSON array of reflection proposals out of a raw model response. Tolerates CoT
/// leakage, markdown code fences, and surrounding prose (same defenses as <see cref="DriftReviewParser"/>).
/// Any malformed or empty response maps to <c>[]</c> so the maintenance run simply mints nothing this
/// pass. Unknown <c>type</c>/<c>valence</c> strings degrade to the safest option (Observation / Neutral)
/// rather than dropping the proposal.
/// </summary>
internal static class ReflectionProposalParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<ReflectionProposal> Parse(string? raw)
    {
        var text = OllamaTextSanitizer.Clean(raw);
        if (text is null) return [];

        var json = ExtractBalancedArray(StripCodeFences(text));
        if (json is null) return [];

        List<ProposalDto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<ProposalDto>>(json, Options);
        }
        catch (JsonException)
        {
            return [];
        }
        if (dtos is null) return [];

        var proposals = new List<ReflectionProposal>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (string.IsNullOrWhiteSpace(dto.Content)) continue;

            var type = ParseEnum(dto.Type, MemoryType.Observation);
            var valence = ParseEnum(dto.Valence, Valence.Neutral);
            var tags = dto.Tags?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];

            proposals.Add(new ReflectionProposal(dto.Content.Trim(), type, valence, tags));
        }
        return proposals;
    }

    /// <summary>Case-insensitive enum parse that degrades to <paramref name="fallback"/> for unknown
    /// names AND for numeric/out-of-range strings (<c>"7"</c> → fallback, not an undefined member) — the
    /// latter is where a bare <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would silently
    /// admit an orphaned value that no per-type query enumerates.</summary>
    private static TEnum ParseEnum<TEnum>(string? raw, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(raw, ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : fallback;

    private static string StripCodeFences(string text)
    {
        text = text.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0) return text;
        text = text[(firstNewline + 1)..];

        var closing = text.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0) text = text[..closing];
        return text.Trim();
    }

    /// <summary>First balanced <c>[...]</c> block, honoring string escapes so a bracket inside a string literal doesn't close it early.</summary>
    private static string? ExtractBalancedArray(string text)
    {
        var start = text.IndexOf('[');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0)
                return text[start..(i + 1)];
        }

        return null;
    }

    private sealed class ProposalDto
    {
        public string? Content { get; set; }
        public string? Type { get; set; }
        public string? Valence { get; set; }
        public List<string>? Tags { get; set; }
    }
}
