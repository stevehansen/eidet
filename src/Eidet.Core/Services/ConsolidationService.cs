using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

public class ConsolidationService
{
    private readonly IEidetStore _store;

    // FadeMem decay parameters per type
    private static readonly Dictionary<MemoryType, (double HalfLifeDays, double Shape)> DecayParams = new()
    {
        [MemoryType.Observation] = (30, 1.2),   // Super-linear: fast fade
        [MemoryType.Insight] = (90, 1.0),        // Linear: standard exponential
        [MemoryType.Procedure] = (365, 0.8),     // Sub-linear: slow fade
        [MemoryType.Heuristic] = (730, 0.7),     // Sub-linear: nearly immortal
    };

    public ConsolidationService(IEidetStore store)
    {
        _store = store;
    }

    public async Task<ConsolidationResult> ConsolidateAsync(string repoId, bool dryRun = false, CancellationToken ct = default)
    {
        var result = new ConsolidationResult();

        // Get recent valid observations not already consolidated
        var observations = await _store.GetTopScoredAsync(
            repoId, [MemoryType.Observation], 200, ct);

        // Filter out observations already derived by an insight
        observations = observations
            .Where(o => o.DerivedFrom.Count == 0 && o.Validity.ValidUntil == null)
            .ToList();

        if (observations.Count < 3)
            return result;

        // Group by tag overlap using union-find
        var groups = GroupByTagOverlap(observations);

        foreach (var group in groups.Where(g => g.Count >= 3))
        {
            var unionTags = group.SelectMany(o => o.Tags).Distinct().ToList();
            var meanImportance = group.Average(o => o.Importance);
            var proposedImportance = Math.Min(1.0f, (float)(meanImportance * 1.2));

            // Use the highest-importance observation as representative
            var representative = group.OrderByDescending(o => o.Importance).First();

            var candidate = new ConsolidationCandidate
            {
                ObservationIds = group.Select(o => o.Id).ToList(),
                Tags = unionTags,
                ProposedContent = representative.Content,
                ProposedImportance = proposedImportance,
            };
            result.Candidates.Add(candidate);

            if (!dryRun)
            {
                // Check existing insights for topic coverage (spec: vector similarity > 0.85)
                var existingInsight = await _store.FindDuplicateAsync(repoId, representative.Content, 0.85f, ct);
                if (existingInsight is not null && existingInsight.Type == MemoryType.Insight)
                {
                    // Boost existing insight's importance instead of creating a new one
                    existingInsight.Importance = Math.Min(1.0f, existingInsight.Importance + 0.05f * group.Count);
                    existingInsight.DerivedFrom = existingInsight.DerivedFrom
                        .Concat(candidate.ObservationIds)
                        .Distinct()
                        .ToList();
                    await _store.UpdateAsync(existingInsight, ct);
                    result.InsightsBoosted++;
                }
                else
                {
                    var now = DateTime.UtcNow;
                    var insight = new MemoryEntry
                    {
                        Id = MemoryIdGenerator.Generate(repoId, MemoryType.Insight, representative.Content, now),
                        RepoId = repoId,
                        Type = MemoryType.Insight,
                        Content = representative.Content,
                        Tags = unionTags,
                        Importance = proposedImportance,
                        Source = "consolidation",
                        Provenance = MemoryProvenance.Consolidation,
                        Confidence = 0.7f,
                        CreatedAt = now,
                        Validity = new Validity { ValidFrom = now },
                        DerivedFrom = candidate.ObservationIds,
                        Entities = EntityExtractor.Extract(representative.Content),
                        OneLiner = EntityExtractor.GenerateHeuristicOneLiner(representative.Content),
                    };
                    await _store.StoreAsync(insight, ct);
                    result.InsightsCreated++;
                }
            }
        }

        return result;
    }

    public async Task<int> ApplyImportanceDecayAsync(string repoId, bool isRepoActive = true, CancellationToken ct = default)
    {
        var updated = 0;

        foreach (var type in Enum.GetValues<MemoryType>())
        {
            var entries = await _store.GetTopScoredAsync(repoId, [type], 500, ct);
            var (halfLife, shape) = DecayParams[type];
            var now = DateTime.UtcNow;

            foreach (var entry in entries)
            {
                // Skip recently accessed (within 7 days)
                var lastTouched = entry.LastAccessedAt ?? entry.CreatedAt;
                if ((now - lastTouched).TotalDays < 7)
                    continue;

                // Skip dormant repos if not active
                if (!isRepoActive)
                    continue;

                // Confidence-adjusted half-life (0.75x to 1.25x)
                var confidenceBoost = 1.0 + (entry.Confidence - 0.5) * 0.5;
                var adjustedHalfLife = halfLife * confidenceBoost;

                var daysSinceCreation = Math.Max(0, (now - entry.CreatedAt).TotalDays);
                var normalizedAge = daysSinceCreation / adjustedHalfLife;
                var shapedAge = Math.Pow(normalizedAge, shape);
                var decayFactor = Math.Pow(2, -shapedAge);

                var decayed = (float)(entry.Importance * decayFactor);
                decayed = Math.Max(0.05f, decayed); // Floor

                // Only update if change is significant (> 1%)
                if (Math.Abs(decayed - entry.Importance) / Math.Max(entry.Importance, 0.01f) < 0.01f)
                    continue;

                entry.Importance = decayed;
                await _store.UpdateAsync(entry, ct);
                updated++;
            }
        }

        return updated;
    }

    internal static List<List<MemoryEntry>> GroupByTagOverlap(List<MemoryEntry> observations)
    {
        var n = observations.Count;
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        // Build tag → index map
        var tagMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++)
        {
            foreach (var tag in observations[i].Tags)
            {
                if (!tagMap.TryGetValue(tag, out var list))
                {
                    list = [];
                    tagMap[tag] = list;
                }
                list.Add(i);
            }
        }

        // Union observations sharing any tag
        foreach (var indices in tagMap.Values)
        {
            for (var i = 1; i < indices.Count; i++)
                Union(indices[0], indices[i]);
        }

        // Collect groups
        var groups = new Dictionary<int, List<MemoryEntry>>();
        for (var i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var group))
            {
                group = [];
                groups[root] = group;
            }
            group.Add(observations[i]);
        }

        return groups.Values.ToList();
    }
}

public class ConsolidationResult
{
    public List<ConsolidationCandidate> Candidates { get; set; } = [];
    public int InsightsCreated { get; set; }
    public int InsightsBoosted { get; set; }
}

public class ConsolidationCandidate
{
    public List<string> ObservationIds { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string ProposedContent { get; set; } = "";
    public float ProposedImportance { get; set; }
}
