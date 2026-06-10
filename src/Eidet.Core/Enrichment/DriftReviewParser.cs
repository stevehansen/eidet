using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Domain;

namespace Eidet.Core.Enrichment;

/// <summary>
/// Parses the strict-JSON drift verdict out of a raw model response. Tolerates CoT leakage,
/// markdown code fences, and surrounding prose. Any malformed response maps to null so the
/// caller skips the entry — it gets retried on a future run.
/// </summary>
internal static class DriftReviewParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static DriftReview? Parse(string? raw)
    {
        var text = OllamaTextSanitizer.Clean(raw);
        if (text is null) return null;

        // Prose can contain brace pairs before the real verdict ("the config {x: 1} changed…"),
        // so every balanced block is tried until one yields a verdict.
        foreach (var json in EnumerateJsonObjects(StripCodeFences(text)))
        {
            DriftDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<DriftDto>(json, Options);
            }
            catch (JsonException)
            {
                continue;
            }

            var verdict = dto?.Verdict?.Trim().ToLowerInvariant() switch
            {
                "ok" => DriftVerdictKind.Ok,
                "stale" => DriftVerdictKind.Stale,
                "contradicted" => DriftVerdictKind.Contradicted,
                "vague" => DriftVerdictKind.Vague,
                _ => (DriftVerdictKind?)null,
            };
            if (verdict is null) continue;

            return new DriftReview
            {
                Verdict = verdict.Value,
                ModelConfidence = Math.Clamp(dto!.Confidence, 0f, 1f),
                Reason = SingleLine(dto.Reason),
                SuggestedFix = dto.SuggestedFix,
            };
        }

        return null;
    }

    /// <summary>Collapses whitespace runs so the reason can be injected into one-line recall warnings.</summary>
    private static string? SingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

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

    private static IEnumerable<string> EnumerateJsonObjects(string text)
    {
        for (var start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            var block = ExtractBalancedBlock(text, start);
            if (block is not null) yield return block;
        }
    }

    private static string? ExtractBalancedBlock(string text, int start)
    {
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
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
                return text[start..(i + 1)];
        }

        return null;
    }

    private sealed class DriftDto
    {
        public string? Verdict { get; set; }
        public float Confidence { get; set; }
        public string? Reason { get; set; }
        [JsonPropertyName("suggested_fix")]
        public string? SuggestedFix { get; set; }
    }
}
