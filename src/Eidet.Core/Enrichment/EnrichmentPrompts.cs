namespace Eidet.Core.Enrichment;

/// <summary>
/// Prompt wording shared by every enrichment backend. Kept in one place so the
/// Ollama-native and OpenAI-compatible adapters never diverge on prompt text.
/// </summary>
internal static class EnrichmentPrompts
{
    public static string Build(EnrichmentRequest request) => request.Kind switch
    {
        EnrichmentPrompt.OneLiner => $"""
            Generate an ultra-compact one-liner summary (~10 words max) of this memory.
            Return ONLY the one-liner, nothing else.

            Memory: {request.Primary}
            """,

        EnrichmentPrompt.Summary => $"""
            Summarize this memory in 1-2 concise sentences for a software developer.
            Return ONLY the summary, nothing else.

            Memory: {request.Primary}
            """,

        EnrichmentPrompt.ForesightHint => $"""
            Given this developer memory, predict WHEN and HOW it will be most useful in the future.
            Write a brief foresight hint (1 sentence) that helps an AI agent know when to surface this memory.
            Return ONLY the hint, nothing else.

            Memory: {request.Primary}
            """,

        EnrichmentPrompt.Entities => $"""
            Extract named entities from this developer memory: project names, package names,
            file paths, class names, function names, API endpoints, configuration keys, error codes.
            Return one entity per line, nothing else. If none found, return empty.

            Text: {request.Primary}
            """,

        EnrichmentPrompt.MergeObservations => BuildMergePrompt(request.Aux ?? []),

        EnrichmentPrompt.DriftReview => BuildDriftReviewPrompt(request.Primary, request.Aux ?? []),

        _ => request.Primary,
    };

    private static string BuildMergePrompt(IReadOnlyList<string> observations)
    {
        var numbered = string.Join("\n", observations.Select((o, i) => $"{i + 1}. {o}"));
        return $"""
            These related developer observations should be merged into a single coherent insight.
            Write a concise insight (2-3 sentences) that captures the essential knowledge.
            Return ONLY the merged insight, nothing else.

            Observations:
            {numbered}
            """;
    }

    private static string BuildDriftReviewPrompt(string memory, IReadOnlyList<string> newerSiblings)
    {
        var siblingSection = newerSiblings.Count == 0
            ? ""
            : $"\n\nNewer memories from the same project:\n{string.Join("\n", newerSiblings.Select((s, i) => $"{i + 1}. {s}"))}";

        return $$"""
            Review this developer memory for drift. Pick exactly one verdict:
            - stale: describes a state that has likely changed, or is time-bound and old
            - contradicted: a newer sibling memory disagrees with it
            - vague: too unspecific to ever act on
            - ok: still sound

            Return STRICT JSON only, nothing else:
            {"verdict":"ok|stale|contradicted|vague","confidence":0.0-1.0,"reason":"<short>","suggested_fix":"<rewrite or null>"}

            Memory:
            {{memory}}{{siblingSection}}
            """;
    }
}
