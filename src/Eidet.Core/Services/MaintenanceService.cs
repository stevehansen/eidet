using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

public class MaintenanceService
{
    private readonly IEidetStore _store;
    private readonly ConsolidationService _consolidation;
    private readonly IEnrichmentService _enrichment;

    public MaintenanceService(IEidetStore store, ConsolidationService consolidation,
        IEnrichmentService? enrichment = null)
    {
        _store = store;
        _consolidation = consolidation;
        _enrichment = enrichment ?? NullEnrichmentService.Instance;
    }

    public async Task<MaintenanceResult> RunAsync(
        string repoId, int observationRetentionDays = 90,
        bool isRepoActive = true, CancellationToken ct = default)
    {
        var result = new MaintenanceResult();
        var now = DateTime.UtcNow;

        // Stage 1: TTL Expiry
        result.ExpiredByTtl = await ExpireTtlAsync(repoId, now, ct);

        // Stage 2: Observation Retention
        result.ExpiredByRetention = await ExpireOldObservationsAsync(repoId, observationRetentionDays, now, ct);

        // Stage 3: Dedup Sweep
        result.DedupMerged = await DedupSweepAsync(repoId, ct);

        // Stage 4: Importance Decay (FadeMem)
        result.DecayUpdated = await _consolidation.ApplyImportanceDecayAsync(repoId, isRepoActive, ct);

        // Stage 5: Orphan Cleanup
        result.OrphansCleaned = await CleanOrphansAsync(repoId, now, ct);

        // Stage 6: Backfill Enrichment (entities + one-liners + Ollama enrichment)
        result.BackfillEnriched = await BackfillEnrichmentAsync(repoId, ct);

        // Stage 6b: Ollama enrichment (summaries, foresight hints — if available)
        result.OllamaEnriched = await OllamaEnrichmentAsync(repoId, ct);

        // Stage 7: Auto-Consolidation
        var consolidationResult = await _consolidation.ConsolidateAsync(repoId, ct: ct);
        result.ConsolidatedInsights = consolidationResult.InsightsCreated;

        result.CompletedAt = DateTime.UtcNow;
        return result;
    }

    private async Task<int> ExpireTtlAsync(string repoId, DateTime now, CancellationToken ct)
    {
        var entries = await _store.GetTopScoredAsync(repoId, Enum.GetValues<MemoryType>(), 500, ct);
        var expired = 0;

        foreach (var entry in entries.Where(e => e.ForgetAfter.HasValue && e.ForgetAfter.Value <= now))
        {
            entry.Validity.ValidUntil = now;
            entry.ForgetReason = "TTL expired";
            await _store.UpdateAsync(entry, ct);
            expired++;
        }

        return expired;
    }

    private async Task<int> ExpireOldObservationsAsync(string repoId, int retentionDays, DateTime now, CancellationToken ct)
    {
        var observations = await _store.GetTopScoredAsync(repoId, [MemoryType.Observation], 500, ct);
        var cutoff = now.AddDays(-retentionDays);
        var graceWindow = retentionDays / 2;
        var expired = 0;

        foreach (var obs in observations.Where(o => o.CreatedAt < cutoff))
        {
            // Skip recently accessed (grace period = half retention window)
            var lastTouched = obs.LastAccessedAt ?? obs.CreatedAt;
            if ((now - lastTouched).TotalDays < graceWindow)
                continue;

            obs.Validity.ValidUntil = now;
            obs.ForgetReason = $"Observation retention ({retentionDays}d)";
            await _store.UpdateAsync(obs, ct);
            expired++;
        }

        return expired;
    }

    private async Task<int> DedupSweepAsync(string repoId, CancellationToken ct)
    {
        var merged = 0;

        foreach (var type in Enum.GetValues<MemoryType>())
        {
            var entries = await _store.GetTopScoredAsync(repoId, [type], 200, ct);

            for (var i = 0; i < entries.Count; i++)
            {
                for (var j = i + 1; j < entries.Count; j++)
                {
                    var similarity = ComputeWordSimilarity(entries[i].Content, entries[j].Content);
                    if (similarity < 0.85f) continue;

                    // Keep higher importance, merge tags and access counts
                    var (keep, discard) = entries[i].Importance >= entries[j].Importance
                        ? (entries[i], entries[j])
                        : (entries[j], entries[i]);

                    keep.AccessCount += discard.AccessCount;
                    foreach (var tag in discard.Tags)
                    {
                        if (!keep.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                            keep.Tags.Add(tag);
                    }
                    await _store.UpdateAsync(keep, ct);

                    discard.Validity.ValidUntil = DateTime.UtcNow;
                    discard.ForgetReason = $"Dedup merged into {keep.Id}";
                    await _store.UpdateAsync(discard, ct);
                    merged++;
                }
            }
        }

        return merged;
    }

    private async Task<int> CleanOrphansAsync(string repoId, DateTime now, CancellationToken ct)
    {
        var entries = await _store.GetTopScoredAsync(repoId, Enum.GetValues<MemoryType>(), 500, ct);
        var cleaned = 0;

        foreach (var entry in entries)
        {
            var isOrphan = false;

            // Empty content
            if (string.IsNullOrWhiteSpace(entry.Content))
                isOrphan = true;

            // Old low-importance system observations
            if (entry.Source == "system" && entry.Importance <= 0.1f && (now - entry.CreatedAt).TotalDays > 30)
                isOrphan = true;

            if (!isOrphan) continue;

            entry.Validity.ValidUntil = now;
            entry.ForgetReason = "Orphan cleanup";
            await _store.UpdateAsync(entry, ct);
            cleaned++;
        }

        return cleaned;
    }

    private async Task<int> BackfillEnrichmentAsync(string repoId, CancellationToken ct)
    {
        var entries = await _store.GetTopScoredAsync(repoId, Enum.GetValues<MemoryType>(), 500, ct);
        var enriched = 0;

        foreach (var entry in entries)
        {
            var changed = false;

            // Backfill missing entities
            if (entry.Entities.Count == 0 && !string.IsNullOrWhiteSpace(entry.Content))
            {
                entry.Entities = EntityExtractor.Extract(entry.Content);
                if (entry.Entities.Count > 0) changed = true;
            }

            // Backfill missing one-liner
            if (string.IsNullOrEmpty(entry.OneLiner) && !string.IsNullOrWhiteSpace(entry.Content))
            {
                entry.OneLiner = EntityExtractor.GenerateHeuristicOneLiner(entry.Content);
                if (!string.IsNullOrEmpty(entry.OneLiner)) changed = true;
            }

            if (changed)
            {
                await _store.UpdateAsync(entry, ct);
                enriched++;
            }
        }

        return enriched;
    }

    private async Task<int> OllamaEnrichmentAsync(string repoId, CancellationToken ct)
    {
        if (!_enrichment.IsAvailable) return 0;

        var entries = await _store.GetTopScoredAsync(repoId, Enum.GetValues<MemoryType>(), 200, ct);
        var enriched = 0;

        foreach (var entry in entries)
        {
            var changed = false;

            // Summary (1-2 sentence)
            if (string.IsNullOrEmpty(entry.Summary) && !string.IsNullOrWhiteSpace(entry.Content))
            {
                var summary = await _enrichment.GenerateSummaryAsync(entry.Content, ct);
                if (!string.IsNullOrEmpty(summary))
                {
                    entry.Summary = summary;
                    changed = true;
                }
            }

            // One-liner upgrade (replace heuristic with LLM if available)
            if (!string.IsNullOrWhiteSpace(entry.Content) && entry.OneLiner == EntityExtractor.GenerateHeuristicOneLiner(entry.Content))
            {
                var oneLiner = await _enrichment.GenerateOneLinerAsync(entry.Content, ct);
                if (!string.IsNullOrEmpty(oneLiner))
                {
                    entry.OneLiner = oneLiner;
                    changed = true;
                }
            }

            // Foresight hint
            if (string.IsNullOrEmpty(entry.ForesightHint) && !string.IsNullOrWhiteSpace(entry.Content))
            {
                var hint = await _enrichment.GenerateForesightHintAsync(entry.Content, ct);
                if (!string.IsNullOrEmpty(hint))
                {
                    entry.ForesightHint = hint;
                    changed = true;
                }
            }

            // LLM entity extraction (supplement regex)
            if (entry.Entities.Count < 2 && !string.IsNullOrWhiteSpace(entry.Content))
            {
                var llmEntities = await _enrichment.ExtractEntitiesAsync(entry.Content, ct);
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

            if (changed)
            {
                await _store.UpdateAsync(entry, ct);
                enriched++;
            }
        }

        return enriched;
    }

    internal static float ComputeWordSimilarity(string a, string b)
    {
        var wordsA = Tokenize(a);
        var wordsB = Tokenize(b);
        if (wordsA.Count == 0 && wordsB.Count == 0) return 1.0f;
        if (wordsA.Count == 0 || wordsB.Count == 0) return 0.0f;

        var intersection = wordsA.Intersect(wordsB, StringComparer.OrdinalIgnoreCase).Count();
        var union = wordsA.Union(wordsB, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0f : (float)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
    {
        return new HashSet<string>(
            text.Split([' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1),
            StringComparer.OrdinalIgnoreCase);
    }
}

public class MaintenanceResult
{
    public int ExpiredByTtl { get; set; }
    public int ExpiredByRetention { get; set; }
    public int DedupMerged { get; set; }
    public int DecayUpdated { get; set; }
    public int OrphansCleaned { get; set; }
    public int BackfillEnriched { get; set; }
    public int OllamaEnriched { get; set; }
    public int ConsolidatedInsights { get; set; }
    public DateTime CompletedAt { get; set; }

    public override string ToString() =>
        $"Maintenance complete: TTL={ExpiredByTtl}, Retention={ExpiredByRetention}, Dedup={DedupMerged}, Decay={DecayUpdated}, Orphans={OrphansCleaned}, Backfill={BackfillEnriched}, Ollama={OllamaEnriched}, Consolidated={ConsolidatedInsights}";
}
