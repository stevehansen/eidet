using System.Text;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Core.Memory;

/// <summary>
/// Read pipeline: hybrid recall (full-text + vector merge, type-budgeted) and the
/// L0+L1 wake-up context. Owns the recall cache reads, fires Pre/PostRecall hooks,
/// and asynchronously bumps access counts on local hits. Mutating callers live in
/// <see cref="MemoryWriter"/> and signal cache invalidation via the shared
/// <see cref="RecallCache"/>.
/// </summary>
internal sealed class MemoryRecall
{
    private readonly IEidetStore _store;
    private readonly LayerService? _layers;
    private readonly IHookRunner _hooks;
    private readonly RecallCache _cache;
    private readonly RepoActivityTracker _activity;
    private readonly Func<int> _stalenessWarningDays;

    public MemoryRecall(
        IEidetStore store,
        LayerService? layers,
        IHookRunner hooks,
        RecallCache cache,
        RepoActivityTracker activity,
        Func<int> stalenessWarningDays)
    {
        _store = store;
        _layers = layers;
        _hooks = hooks;
        _cache = cache;
        _activity = activity;
        _stalenessWarningDays = stalenessWarningDays;
    }

    public async Task<List<MemorySearchResult>> RecallAsync(string repoId, MemoryQuery query, CancellationToken ct)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        _activity.Track(normalizedRepoId);

        var preHook = await _hooks.RunPreHooksAsync(HookEvent.PreRecall, new HookContext
        {
            Event = "pre-recall",
            Repo = normalizedRepoId,
            Data = new { query = query.Text, limit = query.Limit, type = query.Type?.ToString().ToLowerInvariant(), tags = query.Tags },
        }, ct);
        if (!preHook.Allowed)
            return [];

        var cacheKey = RecallCache.ComputeKey(normalizedRepoId, query);
        if (_cache.TryGet(cacheKey, out var cached))
            return cached;

        var repoIds = _layers != null && query.CrossRepo
            ? await _layers.ResolveScopeAsync(normalizedRepoId, query.CrossRepo, ct)
            : new List<string> { normalizedRepoId };

        var textTask = _store.FullTextSearchAsync(repoIds, query, ct);
        var vectorTask = _store.VectorSearchAsync(repoIds, query, ct);
        await Task.WhenAll(textTask, vectorTask);

        var merged = new List<MemorySearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in textTask.Result)
        {
            if (seen.Add(entry.Id))
                merged.Add(RecallScoring.ToSearchResult(entry, score: 1.0f));
        }

        foreach (var entry in vectorTask.Result)
        {
            if (seen.Add(entry.Id))
                merged.Add(RecallScoring.ToSearchResult(entry, score: 0.9f));
        }

        var now = DateTime.UtcNow;
        var staleAfter = _stalenessWarningDays();
        foreach (var result in merged)
        {
            if (!string.Equals(result.RepoId, normalizedRepoId, StringComparison.OrdinalIgnoreCase))
                result.Score *= 0.8f;

            result.AgeDays = (int)(now - result.CreatedAt).TotalDays;
            if (result.AgeDays >= staleAfter)
                result.StalenessWarning = $"[stale: {result.AgeDays}d ago — verify before acting]";
        }

        var budgeted = RecallScoring.ApplyTypeBudgets(merged, query.Limit);

        _ = BumpAccessCountsAsync(budgeted, normalizedRepoId, ct);

        _ = _hooks.RunPostHooksAsync(HookEvent.PostRecall, new HookContext
        {
            Event = "post-recall",
            Repo = normalizedRepoId,
            Data = new { query = query.Text, resultCount = budgeted.Count },
        }, ct);

        _cache.Set(cacheKey, budgeted);

        return budgeted;
    }

    public async Task<string> GetContextAsync(string repoId, int maxTokens, CancellationToken ct)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        _activity.Track(normalizedRepoId);

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

        var remainingTokens = maxTokens - RecallScoring.EstimateTokens(sb.Length);
        if (remainingTokens <= 0)
            return sb.ToString();

        // L1: Top-K scored memories
        var candidates = await _store.GetTopScoredAsync(
            normalizedRepoId,
            [MemoryType.Insight, MemoryType.Procedure, MemoryType.Heuristic],
            60,
            ct);

        var now = DateTime.UtcNow;
        var scored = candidates
            .Select(e => new { Entry = e, Score = RecallScoring.ComputeL1Score(e, now) })
            .OrderByDescending(x => x.Score)
            .ToList();

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
            var withinBudget = e.Type switch
            {
                MemoryType.Insight => insightCount < insightBudget,
                MemoryType.Procedure => procedureCount < procedureBudget,
                MemoryType.Heuristic => heuristicCount < heuristicBudget,
                _ => false,
            };
            if (!withinBudget) continue;

            var typePrefix = e.Type switch
            {
                MemoryType.Insight => "[I]",
                MemoryType.Procedure => "[P]",
                MemoryType.Heuristic => "[H]",
                _ => "[O]",
            };

            var content = e.OneLiner ?? e.Summary ?? e.Content;
            var line = $"{typePrefix} {content}";

            var lineTokens = RecallScoring.EstimateTokens(line.Length);
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

    private async Task BumpAccessCountsAsync(List<MemorySearchResult> results, string repoId, CancellationToken ct)
    {
        try
        {
            foreach (var result in results)
            {
                if (!string.Equals(result.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var entry = await _store.GetAsync(result.Id, ct);
                if (entry is null) continue;

                entry.AccessCount++;
                entry.LastAccessedAt = DateTime.UtcNow;
                await _store.UpdateAsync(entry, ct);
            }
        }
        catch { /* Non-critical — don't fail recall for access tracking */ }
    }
}
