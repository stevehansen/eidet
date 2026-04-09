using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Eidet.Core.Domain;
using Eidet.Core.Gates;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

public class MemoryService
{
    private const float DuplicateThreshold = 0.92f;
    private const double RecencyHalfLifeDays = 7.0;
    private const int CacheMaxEntries = 100;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IEidetStore _store;
    private readonly ConcurrentDictionary<string, CacheEntry> _recallCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastActiveDate = new();

    public int StalenessWarningDays { get; set; } = 7;

    public MemoryService(IEidetStore store)
    {
        _store = store;
    }

    // ─── Store ───────────────────────────────────────────────────────────

    public async Task<StoreResult> StoreAsync(
        string repoId,
        string content,
        MemoryType type,
        List<string>? tags = null,
        float importance = 0.5f,
        string source = "claude-session",
        string? sessionId = null,
        string? supersedes = null,
        MemoryProvenance? provenance = null,
        CancellationToken ct = default)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        TrackRepoActivity(normalizedRepoId);

        // Gate 1: Secret scanning
        var secretResult = SecretScanner.Scan(content);
        if (!secretResult.Passed)
            return StoreResult.Rejected(secretResult.Reason!);

        // Gate 2: Signal gate
        var signalResult = SignalGate.Check(content, type);
        if (!signalResult.Passed)
            return StoreResult.Rejected(signalResult.Reason!);

        // Resolve provenance
        var resolvedProvenance = provenance ?? ResolveProvenance(source);

        // Duplicate detection
        var duplicate = await _store.FindDuplicateAsync(normalizedRepoId, content, DuplicateThreshold, ct);
        if (duplicate is not null)
            return StoreResult.Duplicate(duplicate.Id);

        // Handle supersession
        if (!string.IsNullOrEmpty(supersedes))
        {
            var old = await _store.GetAsync(supersedes, ct);
            if (old is not null)
            {
                old.IsLatest = false;
                old.Validity.ValidUntil = DateTime.UtcNow;
                old.ForgetReason = "Superseded by new memory";
                await _store.UpdateAsync(old, ct);
            }
        }

        var now = DateTime.UtcNow;
        var entry = new MemoryEntry
        {
            Id = MemoryIdGenerator.Generate(normalizedRepoId, type, content, now),
            RepoId = normalizedRepoId,
            Type = type,
            Content = content,
            Tags = tags ?? [],
            Importance = Math.Clamp(importance, 0f, 1f),
            Source = source,
            SourceSessionId = sessionId,
            CreatedAt = now,
            Validity = new Validity { ValidFrom = now },
            ParentMemoryId = supersedes,
            IsLatest = true,
            Provenance = resolvedProvenance,
            Confidence = resolvedProvenance == MemoryProvenance.AgentInferred ? 0.6f : 0.7f,
            Entities = EntityExtractor.Extract(content),
            OneLiner = EntityExtractor.GenerateHeuristicOneLiner(content),
        };

        var id = await _store.StoreAsync(entry, ct);
        InvalidateCache();
        return StoreResult.Stored(id);
    }

    // ─── Recall ──────────────────────────────────────────────────────────

    public async Task<List<MemorySearchResult>> RecallAsync(
        string repoId,
        MemoryQuery query,
        CancellationToken ct = default)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        TrackRepoActivity(normalizedRepoId);

        // Check cache
        var cacheKey = ComputeCacheKey(normalizedRepoId, query);
        if (_recallCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
            return cached.Results;

        // Resolve repo scope
        var repoIds = new List<string> { normalizedRepoId };
        // TODO: Add cross-repo linked repos when LayerService is implemented

        // Parallel hybrid search
        var textTask = _store.FullTextSearchAsync(repoIds, query, ct);
        var vectorTask = _store.VectorSearchAsync(repoIds, query, ct);
        await Task.WhenAll(textTask, vectorTask);

        var textResults = textTask.Result;
        var vectorResults = vectorTask.Result;

        // Merge and deduplicate
        var merged = new List<MemorySearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in textResults)
        {
            if (seen.Add(entry.Id))
                merged.Add(ToSearchResult(entry, score: 1.0f));
        }

        foreach (var entry in vectorResults)
        {
            if (seen.Add(entry.Id))
                merged.Add(ToSearchResult(entry, score: 0.9f));
        }

        // Post-processing
        var now = DateTime.UtcNow;
        foreach (var result in merged)
        {
            // Cross-repo de-boost
            if (!string.Equals(result.RepoId, normalizedRepoId, StringComparison.OrdinalIgnoreCase))
                result.Score *= 0.8f;

            // Staleness warning
            result.AgeDays = (int)(now - result.CreatedAt).TotalDays;
            if (result.AgeDays >= StalenessWarningDays)
                result.StalenessWarning = $"[stale: {result.AgeDays}d ago — verify before acting]";
        }

        // Apply type diversity budgets
        var budgeted = ApplyTypeBudgets(merged, query.Limit);

        // Cache results
        EvictCacheIfNeeded();
        _recallCache[cacheKey] = new CacheEntry(budgeted);

        return budgeted;
    }

    // ─── Context (L0 + L1) ───────────────────────────────────────────────

    public async Task<string> GetContextAsync(
        string repoId,
        int maxTokens = 600,
        CancellationToken ct = default)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        TrackRepoActivity(normalizedRepoId);

        var sb = new StringBuilder();

        // L0: Identity
        var counts = await _store.GetCountsByTypeAsync(normalizedRepoId, ct);
        var totalMemories = counts.Values.Sum();

        sb.Append($"[Memory: {totalMemories} entries");
        foreach (var (type, count) in counts.OrderBy(kv => kv.Key))
        {
            if (count > 0)
                sb.Append($", {count} {type.ToString().ToLowerInvariant()}s");
        }
        sb.AppendLine("]");

        var remainingTokens = maxTokens - EstimateTokens(sb.Length);
        if (remainingTokens <= 0)
            return sb.ToString();

        // L1: Top-K scored memories
        var candidates = await _store.GetTopScoredAsync(
            normalizedRepoId,
            [MemoryType.Insight, MemoryType.Procedure, MemoryType.Heuristic],
            60, // Over-fetch for scoring and budgeting
            ct);

        var now = DateTime.UtcNow;
        var scored = candidates
            .Select(e => new { Entry = e, Score = ComputeL1Score(e, now) })
            .OrderByDescending(x => x.Score)
            .ToList();

        // Apply L1 type budgets
        const int maxItems = 20;
        var insightBudget = (int)Math.Ceiling(maxItems * 0.50);
        var procedureBudget = (int)Math.Ceiling(maxItems * 0.30);
        var heuristicBudget = maxItems - insightBudget - procedureBudget;

        var insightCount = 0;
        var procedureCount = 0;
        var heuristicCount = 0;

        foreach (var item in scored)
        {
            var e = item.Entry;
            var budget = e.Type switch
            {
                MemoryType.Insight => insightCount < insightBudget,
                MemoryType.Procedure => procedureCount < procedureBudget,
                MemoryType.Heuristic => heuristicCount < heuristicBudget,
                _ => false,
            };
            if (!budget) continue;

            var typePrefix = e.Type switch
            {
                MemoryType.Insight => "[I]",
                MemoryType.Procedure => "[P]",
                MemoryType.Heuristic => "[H]",
                _ => "[O]",
            };

            var content = e.OneLiner ?? e.Summary ?? e.Content;
            var line = $"{typePrefix} {content}";

            var lineTokens = EstimateTokens(line.Length);
            if (lineTokens > remainingTokens)
                break;

            sb.AppendLine(line);
            remainingTokens -= lineTokens;

            switch (e.Type)
            {
                case MemoryType.Insight: insightCount++; break;
                case MemoryType.Procedure: procedureCount++; break;
                case MemoryType.Heuristic: heuristicCount++; break;
            }
        }

        return sb.ToString();
    }

    // ─── Forget ──────────────────────────────────────────────────────────

    public async Task<bool> ForgetAsync(
        string id,
        string? reason = null,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var forgotten = await _store.ForgetAsync(id, ct);
        if (!forgotten) return false;

        if (!string.IsNullOrEmpty(reason))
        {
            var original = await _store.GetAsync(id, ct);
            if (original is not null)
            {
                original.ForgetReason = reason;
                await _store.UpdateAsync(original, ct);

                // Audit trail: system observation
                var now = DateTime.UtcNow;
                var observation = new MemoryEntry
                {
                    Id = MemoryIdGenerator.Generate(original.RepoId, MemoryType.Observation, reason, now),
                    RepoId = original.RepoId,
                    Type = MemoryType.Observation,
                    Content = $"Forgot memory [{id}]: {reason}",
                    Source = "system",
                    SourceSessionId = sessionId,
                    CreatedAt = now,
                    Validity = new Validity { ValidFrom = now },
                    Importance = 0.1f,
                    DerivedFrom = [id],
                };
                await _store.StoreAsync(observation, ct);
            }
        }

        InvalidateCache();
        return true;
    }

    // ─── Feedback ────────────────────────────────────────────────────────

    public async Task<bool> ApplyFeedbackAsync(
        string memoryId, bool wasUsed, CancellationToken ct = default)
    {
        var entry = await _store.GetAsync(memoryId, ct);
        if (entry is null) return false;

        if (wasUsed)
        {
            entry.EchoCount++;
            entry.Importance = Math.Min(1.0f, entry.Importance + 0.05f);
            entry.Confidence = Math.Min(1.0f, entry.Confidence + 0.1f);
        }
        else
        {
            entry.FizzleCount++;
            entry.Importance = Math.Max(0.05f, entry.Importance - 0.1f);
            entry.Confidence = Math.Max(0.0f, entry.Confidence - 0.15f);
        }

        entry.LastAccessedAt = DateTime.UtcNow;
        entry.AccessCount++;
        await _store.UpdateAsync(entry, ct);
        InvalidateCache();
        return true;
    }

    // ─── History ─────────────────────────────────────────────────────────

    public async Task<List<MemoryEntry>> GetVersionChainAsync(
        string memoryId, CancellationToken ct = default)
    {
        var chain = new List<MemoryEntry>();
        var current = await _store.GetAsync(memoryId, ct);
        if (current is null) return chain;

        chain.Add(current);

        var visited = new HashSet<string> { memoryId };
        while (!string.IsNullOrEmpty(current.ParentMemoryId) && visited.Add(current.ParentMemoryId))
        {
            current = await _store.GetAsync(current.ParentMemoryId, ct);
            if (current is null) break;
            chain.Add(current);
        }

        return chain;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static double ComputeL1Score(MemoryEntry entry, DateTime now)
    {
        var importance = (double)entry.Importance;
        var confidence = (double)entry.Confidence;

        var daysSinceCreation = Math.Max(0, (now - entry.CreatedAt).TotalDays);
        var recency = Math.Exp(-0.693 * daysSinceCreation / RecencyHalfLifeDays);

        var frequency = Math.Min(1.0, entry.AccessCount / 10.0);

        return importance * 0.3 + confidence * 0.15 + recency * 0.25 + frequency * 0.3;
    }

    private static List<MemorySearchResult> ApplyTypeBudgets(List<MemorySearchResult> results, int limit)
    {
        var insightBudget = (int)Math.Ceiling(limit * 0.40);
        var observationBudget = (int)Math.Ceiling(limit * 0.25);
        var procedureBudget = (int)Math.Ceiling(limit * 0.20);
        var heuristicBudget = Math.Max(1, limit - insightBudget - observationBudget - procedureBudget);

        var budgeted = new List<MemorySearchResult>();
        var typeCounts = new Dictionary<MemoryType, int>
        {
            [MemoryType.Insight] = 0,
            [MemoryType.Observation] = 0,
            [MemoryType.Procedure] = 0,
            [MemoryType.Heuristic] = 0,
        };

        var budgets = new Dictionary<MemoryType, int>
        {
            [MemoryType.Insight] = insightBudget,
            [MemoryType.Observation] = observationBudget,
            [MemoryType.Procedure] = procedureBudget,
            [MemoryType.Heuristic] = heuristicBudget,
        };

        // First pass: fill within budgets
        foreach (var result in results.OrderByDescending(r => r.Score))
        {
            if (budgeted.Count >= limit) break;
            if (typeCounts[result.Type] < budgets[result.Type])
            {
                budgeted.Add(result);
                typeCounts[result.Type]++;
            }
        }

        // Second pass: fill remaining slots with any type
        foreach (var result in results.OrderByDescending(r => r.Score))
        {
            if (budgeted.Count >= limit) break;
            if (!budgeted.Contains(result))
                budgeted.Add(result);
        }

        return budgeted;
    }

    private static MemorySearchResult ToSearchResult(MemoryEntry entry, float score) => new()
    {
        Id = entry.Id,
        RepoId = entry.RepoId,
        Type = entry.Type,
        Content = entry.Content,
        Summary = entry.Summary,
        Tags = entry.Tags,
        Entities = entry.Entities,
        Importance = entry.Importance,
        OneLiner = entry.OneLiner,
        CreatedAt = entry.CreatedAt,
        Score = score,
        IsSuperseded = !entry.IsLatest,
    };

    private static MemoryProvenance ResolveProvenance(string source) => source switch
    {
        "user" => MemoryProvenance.UserStated,
        "claude-session" => MemoryProvenance.AgentInferred,
        "consolidation" => MemoryProvenance.Consolidation,
        "intake" => MemoryProvenance.Intake,
        "bundle" => MemoryProvenance.Bundle,
        "system" => MemoryProvenance.System,
        _ => MemoryProvenance.AgentInferred,
    };

    private static int EstimateTokens(int charCount) => (int)Math.Ceiling(charCount / 4.0);

    private static string ComputeCacheKey(string repoId, MemoryQuery query)
    {
        var raw = $"{repoId}|{query.Text}|{query.Type}|{string.Join(",", query.Tags)}|{query.Limit}|{query.IncludeExpired}|{query.CrossRepo}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }

    private void InvalidateCache() => _recallCache.Clear();

    private void EvictCacheIfNeeded()
    {
        if (_recallCache.Count < CacheMaxEntries) return;

        var expiredKeys = _recallCache.Where(kv => kv.Value.IsExpired).Select(kv => kv.Key).ToList();
        foreach (var key in expiredKeys)
            _recallCache.TryRemove(key, out _);

        if (_recallCache.Count >= CacheMaxEntries)
        {
            var toRemove = _recallCache.OrderBy(kv => kv.Value.CreatedAt)
                .Take(_recallCache.Count - CacheMaxEntries + 10)
                .Select(kv => kv.Key).ToList();
            foreach (var key in toRemove)
                _recallCache.TryRemove(key, out _);
        }
    }

    private void TrackRepoActivity(string repoId) =>
        _lastActiveDate[repoId] = DateTime.UtcNow;

    public bool IsRepoActive(string repoId, int withinDays = 7)
    {
        if (_lastActiveDate.TryGetValue(repoId, out var lastActive))
            return (DateTime.UtcNow - lastActive).TotalDays <= withinDays;
        return false;
    }

    private sealed class CacheEntry(List<MemorySearchResult> results)
    {
        public List<MemorySearchResult> Results { get; } = results;
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public bool IsExpired => DateTime.UtcNow - CreatedAt > CacheTtl;
    }
}
