using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Enrichment;

/// <summary>
/// Enrichment facade. Owns the "what fields does a memory need" policy and the merge
/// semantics for consolidation. Hides prompt wording, HTTP transport, health caching,
/// and Ollama/Gemma CoT quirks behind an <see cref="IEnrichmentPort"/>.
/// </summary>
public sealed class EnrichmentService : IDisposable
{
    private readonly IEnrichmentPort _port;
    private readonly bool _ownsPort;

    public EnrichmentService(IEnrichmentPort port, bool ownsPort = true, string? modelName = null)
    {
        _port = port;
        _ownsPort = ownsPort;
        ModelName = modelName;
    }

    public static EnrichmentService CreateOllama(string ollamaUrl, string model)
        => new(new OllamaEnrichmentAdapter(ollamaUrl, model), modelName: model);

    public static EnrichmentService CreateNull()
        => new(new NullEnrichmentAdapter());

    /// <summary>
    /// Builds the enrichment service the config asks for: disabled → null adapter,
    /// OpenAI-compatible provider → <see cref="OpenAiEnrichmentAdapter"/>, otherwise Ollama.
    /// </summary>
    public static EnrichmentService CreateFromConfig(EnrichmentConfig cfg)
    {
        if (!cfg.Enabled) return CreateNull();
        return cfg.Provider == EnrichmentProvider.OpenAiCompatible
            ? new EnrichmentService(new OpenAiEnrichmentAdapter(cfg.Url, cfg.Model), modelName: cfg.Model)
            : CreateOllama(cfg.Url, cfg.Model);
    }

    public bool IsAvailable => _port.IsAvailable;

    /// <summary>Model identifier recorded on drift reviews; null when enrichment is disabled.</summary>
    public string? ModelName { get; }

    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => _port.CheckHealthAsync(ct);

    /// <summary>
    /// Fills missing enrichment fields on the entry in place. Returns true if anything
    /// changed. Skips cleanly when the port is unavailable or the entry has no content.
    /// </summary>
    public async Task<bool> EnrichMemoryAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;
        if (string.IsNullOrWhiteSpace(entry.Content)) return false;

        var changed = false;

        if (string.IsNullOrEmpty(entry.Summary))
        {
            var summary = await GenerateAsync(EnrichmentPrompt.Summary, entry.Content, ct);
            if (!string.IsNullOrEmpty(summary))
            {
                entry.Summary = summary;
                changed = true;
            }
        }

        if (entry.OneLiner == EntityExtractor.GenerateHeuristicOneLiner(entry.Content))
        {
            var oneLiner = await GenerateAsync(EnrichmentPrompt.OneLiner, entry.Content, ct);
            if (!string.IsNullOrEmpty(oneLiner))
            {
                entry.OneLiner = oneLiner;
                changed = true;
            }
        }

        if (string.IsNullOrEmpty(entry.ForesightHint))
        {
            var hint = await GenerateAsync(EnrichmentPrompt.ForesightHint, entry.Content, ct);
            if (!string.IsNullOrEmpty(hint))
            {
                entry.ForesightHint = hint;
                changed = true;
            }
        }

        if (entry.Entities.Count < 2)
        {
            var llmEntities = await ExtractEntitiesAsync(entry.Content, ct);
            if (llmEntities.Count > 0)
            {
                var existing = new HashSet<string>(entry.Entities, StringComparer.OrdinalIgnoreCase);
                foreach (var e in llmEntities)
                {
                    if (existing.Add(e))
                        entry.Entities.Add(e);
                }
                changed = true;
            }
        }

        return changed;
    }

    public Task<string?> GenerateAsync(EnrichmentPrompt kind, string content, CancellationToken ct = default)
        => _port.CompleteAsync(new EnrichmentRequest(kind, content), ct);

    public async Task<List<string>> ExtractEntitiesAsync(string content, CancellationToken ct = default)
    {
        var raw = await GenerateAsync(EnrichmentPrompt.Entities, content, ct);
        if (string.IsNullOrWhiteSpace(raw)) return [];

        return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.Length > 1 && e.Length < 100)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<string?> MergeObservationsAsync(IReadOnlyList<string> observations, CancellationToken ct = default)
        => _port.CompleteAsync(new EnrichmentRequest(EnrichmentPrompt.MergeObservations, string.Empty, observations), ct);

    /// <summary>
    /// Asks the model whether a memory has drifted (stale/contradicted/vague) given the
    /// one-liners of newer sibling memories. Returns null when the port is unavailable,
    /// the entry has no content, or the response cannot be parsed — the caller skips the
    /// entry and it gets retried on a future run.
    /// </summary>
    public async Task<DriftReview?> ReviewDriftAsync(MemoryEntry entry,
        IReadOnlyList<string> newerSiblingOneLiners, DateTime now, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        if (string.IsNullOrWhiteSpace(entry.Content)) return null;

        // EnrichmentRequest only carries strings, so age/now are folded into Primary here;
        // the prompt wording itself stays in EnrichmentPrompts.
        var ageDays = (int)Math.Max(0, (now - entry.CreatedAt).TotalDays);
        var primary = $"""
            Type: {entry.Type}
            Age: {ageDays} days (created {entry.CreatedAt:yyyy-MM-dd}, today is {now:yyyy-MM-dd})
            Content: {entry.Content}
            """;

        var raw = await _port.CompleteAsync(
            new EnrichmentRequest(EnrichmentPrompt.DriftReview, primary, newerSiblingOneLiners), ct);

        var review = DriftReviewParser.Parse(raw);
        if (review is null) return null;

        review.ReviewedAt = now;
        review.Model = ModelName ?? "";
        return review;
    }

    /// <summary>
    /// Asks the model to distil net-new memory candidates from feedback residue (the Reflector's one
    /// LLM call). Returns <c>[]</c> when the port is unavailable or the residue is empty — the caller
    /// mints nothing. Proposals carry advisory content only; the engine stamps all trust-bearing fields.
    /// </summary>
    public async Task<IReadOnlyList<ReflectionProposal>> ProposeReflectionsAsync(
        ReflectionResidue residue, CancellationToken ct = default)
    {
        if (!IsAvailable || residue.IsEmpty) return [];

        var raw = await GenerateAsync(EnrichmentPrompt.Reflect, EnrichmentPrompts.RenderResidue(residue), ct);
        return ReflectionProposalParser.Parse(raw);
    }

    public void Dispose()
    {
        if (_ownsPort && _port is IDisposable d) d.Dispose();
    }
}
