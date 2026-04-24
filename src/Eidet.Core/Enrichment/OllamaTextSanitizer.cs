namespace Eidet.Core.Enrichment;

/// <summary>
/// Cleans Ollama/Gemma chain-of-thought leakage from model output.
/// Used inside the Ollama adapter on fresh responses, and by the
/// EnrichmentCleanupStage to retroactively clean corrupted fields stored
/// before CoT stripping existed.
/// </summary>
internal static class OllamaTextSanitizer
{
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        const string channelMarker = "<channel|>";
        var lastIdx = text.LastIndexOf(channelMarker, StringComparison.Ordinal);
        if (lastIdx >= 0)
        {
            text = text[(lastIdx + channelMarker.Length)..].Trim();
            var secondMarker = text.IndexOf(channelMarker, StringComparison.Ordinal);
            if (secondMarker > 0)
                text = text[..secondMarker].Trim();
        }

        if (text.Contains("<think>", StringComparison.OrdinalIgnoreCase))
        {
            var thinkEnd = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (thinkEnd >= 0)
                text = text[(thinkEnd + "</think>".Length)..].Trim();
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
