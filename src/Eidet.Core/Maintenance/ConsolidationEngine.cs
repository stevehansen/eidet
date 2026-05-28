using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Maintenance;

/// <summary>
/// Consolidation engine: groups observations by tag overlap and either creates new
/// insights or boosts existing ones. Exposed publicly so API / MCP / scheduler can
/// run consolidation in dry-run or stand-alone mode without spinning up the full
/// maintenance pipeline. Also owns per-type FadeMem decay application.
/// </summary>
public sealed class ConsolidationEngine
{
    private readonly IEidetStore _store;
    private readonly EnrichmentService _enrichment;
    private readonly MemoryService _memory;

    public ConsolidationEngine(IEidetStore store, EnrichmentService? enrichment, MemoryService memory)
    {
        _store = store;
        _enrichment = enrichment ?? EnrichmentService.CreateNull();
        _memory = memory;
    }

    public async Task<ConsolidationResult> ConsolidateAsync(string repoId, bool dryRun = false, CancellationToken ct = default)
    {
        var result = new ConsolidationResult();

        var observations = await _store.GetTopScoredAsync(repoId, [MemoryType.Observation], 200, ct);
        observations = observations
            .Where(o => o.DerivedFrom.Count == 0 && o.Validity.ValidUntil == null)
            .ToList();

        if (observations.Count < 3) return result;

        var groups = TagOverlapGrouper.Group(observations);

        await _memory.RunBulkAsync(async ctx =>
        {
            foreach (var group in groups.Where(g => g.Count >= 3))
            {
                var unionTags = group.SelectMany(o => o.Tags).Distinct().ToList();
                var meanImportance = group.Average(o => o.Importance);
                var proposedImportance = Math.Min(1.0f, (float)(meanImportance * 1.2));
                var representative = group.OrderByDescending(o => o.Importance).First();

                var candidate = new ConsolidationCandidate
                {
                    ObservationIds = group.Select(o => o.Id).ToList(),
                    Tags = unionTags,
                    ProposedContent = representative.Content,
                    ProposedImportance = proposedImportance,
                };
                result.Candidates.Add(candidate);

                if (dryRun) continue;

                var existingInsight = await _store.FindDuplicateAsync(repoId, representative.Content, 0.85f, ct);
                if (existingInsight is not null && existingInsight.Type == MemoryType.Insight)
                {
                    existingInsight.Importance = Math.Min(1.0f, existingInsight.Importance + 0.05f * group.Count);
                    existingInsight.DerivedFrom = existingInsight.DerivedFrom
                        .Concat(candidate.ObservationIds)
                        .Distinct()
                        .ToList();
                    await ctx.WriteAsync(existingInsight, ct);
                    result.InsightsBoosted++;
                }
                else
                {
                    var mergedContent = representative.Content;
                    if (group.Count > 5 && _enrichment.IsAvailable)
                    {
                        var merged = await _enrichment.MergeObservationsAsync(
                            group.Select(o => o.Content).ToList(), ct);
                        if (!string.IsNullOrEmpty(merged))
                            mergedContent = merged;
                    }

                    var now = DateTime.UtcNow;
                    var insight = new MemoryEntry
                    {
                        Id = MemoryIdGenerator.Generate(repoId, MemoryType.Insight, mergedContent, now),
                        RepoId = repoId,
                        Type = MemoryType.Insight,
                        Content = mergedContent,
                        Tags = unionTags,
                        Importance = proposedImportance,
                        Source = "consolidation",
                        Provenance = MemoryProvenance.Consolidation,
                        Confidence = 0.7f,
                        CreatedAt = now,
                        Validity = new Validity { ValidFrom = now },
                        DerivedFrom = candidate.ObservationIds,
                        Entities = EntityExtractor.Extract(mergedContent),
                        OneLiner = EntityExtractor.GenerateHeuristicOneLiner(mergedContent),
                    };
                    await ctx.StoreNewAsync(insight, ct);
                    result.InsightsCreated++;
                }
            }
            return 0;
        }, new BulkOptions { OperationName = "consolidate" }, ct);

        return result;
    }

    public async Task<int> ApplyImportanceDecayAsync(string repoId, bool isRepoActive = true, CancellationToken ct = default)
    {
        if (!isRepoActive) return 0;

        var now = DateTime.UtcNow;
        var changed = new List<MemoryEntry>();

        foreach (var type in Enum.GetValues<MemoryType>())
        {
            var entries = await _store.GetTopScoredAsync(repoId, [type], 500, ct);

            foreach (var entry in entries)
            {
                var lastTouched = entry.LastAccessedAt ?? entry.CreatedAt;
                if ((now - lastTouched).TotalDays < 7) continue;

                var daysSinceCreation = Math.Max(0, (now - entry.CreatedAt).TotalDays);
                var decayed = FadeMemCurve.Decay(entry.Importance, entry.Confidence, daysSinceCreation, type);

                if (Math.Abs(decayed - entry.Importance) / Math.Max(entry.Importance, 0.01f) < 0.01f)
                    continue;

                entry.Importance = decayed;
                changed.Add(entry);
            }
        }

        if (changed.Count == 0) return 0;
        await _memory.UpdateManyAsync(changed, ct);
        return changed.Count;
    }
}

public sealed class ConsolidationResult
{
    public List<ConsolidationCandidate> Candidates { get; set; } = [];
    public int InsightsCreated { get; set; }
    public int InsightsBoosted { get; set; }
}

public sealed class ConsolidationCandidate
{
    public List<string> ObservationIds { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string ProposedContent { get; set; } = "";
    public float ProposedImportance { get; set; }
}
