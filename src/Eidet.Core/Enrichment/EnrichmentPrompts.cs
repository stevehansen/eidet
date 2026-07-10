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

        EnrichmentPrompt.Reflect => BuildReflectPrompt(request.Primary),

        _ => request.Primary,
    };

    /// <summary>
    /// Renders assembled feedback residue into the prompt body. Mirrors how <see cref="ReviewDrift"/>
    /// folds an entry's fields into <c>Primary</c>: the engine hands the whole residue here, the wording
    /// stays in <see cref="BuildReflectPrompt"/>. Only content the reflection is derived from is shown —
    /// never scores or provenance the model could echo back and launder.
    /// </summary>
    public static string RenderResidue(ReflectionResidue residue)
    {
        var sb = new System.Text.StringBuilder();

        if (residue.EchoedMemories.Count > 0)
        {
            sb.AppendLine("Memories that proved useful (repeatedly recalled and confirmed):");
            foreach (var m in residue.EchoedMemories)
                sb.AppendLine($"- [{m.Type}] {m.OneLiner ?? m.Content}");
            sb.AppendLine();
        }

        if (residue.ResolvedEnds.Count > 0)
        {
            sb.AppendLine("Open work that was completed:");
            foreach (var e in residue.ResolvedEnds)
                sb.AppendLine($"- {e.Note}{(string.IsNullOrEmpty(e.ResolutionNote) ? "" : $" → {e.ResolutionNote}")}");
            sb.AppendLine();
        }

        if (residue.Contradicted.Count > 0)
        {
            sb.AppendLine("Memories flagged as contradicted by newer knowledge:");
            foreach (var m in residue.Contradicted)
                sb.AppendLine($"- [{m.Type}] {m.OneLiner ?? m.Content}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

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

    private static string BuildReflectPrompt(string residue) => $$"""
        You are reflecting on a software project's memory to distil durable, reusable lessons.
        Below is recent POSITIVE signal: memories that proved useful, work that got completed, and
        claims that were contradicted. Synthesize NET-NEW higher-level knowledge that is not already
        stated verbatim — patterns, insights, do/don't lessons, or procedures worth remembering.

        Rules:
        - Only propose knowledge genuinely supported by the residue. If nothing new is warranted, return [].
        - Each item must be specific and self-contained (a stranger to this project could act on it).
        - Do NOT restate a single residue line; only generalize across signal.

        Return STRICT JSON only — an array of objects, nothing else:
        [{"content":"<the memory text>","type":"observation|insight|procedure|heuristic","valence":"neutral|affirming|refuting|cautionary","tags":["..."]}]

        Residue:
        {{residue}}
        """;
}
