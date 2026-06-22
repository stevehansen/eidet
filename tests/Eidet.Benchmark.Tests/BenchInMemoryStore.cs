using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Benchmark.Tests;

/// <summary>
/// Minimal in-memory <see cref="IEidetStore"/> for the FAMA forget/supersede behavioral guard.
/// Replicated here (rather than depending on <c>Eidet.Core.Tests</c>'s internal fake) and made to
/// match the RavenDB store's <em>visibility</em> contract on the surfaces the guard exercises:
/// the recall arms exclude anything with <c>Validity.ValidUntil</c> set (the filter
/// <c>RavenEidetStore.ApplyFilters</c> applies), and <see cref="GetTopScoredAsync"/> /
/// <see cref="GetCountsByTypeAsync"/> additionally require <c>IsLatest</c> — so a forgotten or
/// superseded entry is invisible to recall and context exactly as in production.
///
/// Layer support is real enough to fan a cross-repo recall across a mounted layer's
/// <c>ApplicableRepos</c>, so the guard can prove stale-suppression holds across the repo union.
/// </summary>
internal sealed class BenchInMemoryStore : IEidetStore
{
    private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MemoryLayer> _layers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _entries.TryGetValue(id, out var e);
            return Task.FromResult(e);
        }
    }

    public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        lock (_lock) _entries[entry.Id] = entry;
        return Task.FromResult(entry.Id);
    }

    public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        lock (_lock) _entries[entry.Id] = entry;
        return Task.CompletedTask;
    }

    public Task<bool> ForgetAsync(string id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var e)) return Task.FromResult(false);
            e.Validity.ValidUntil = DateTime.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<List<MemoryEntry>> FullTextSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var results = _entries.Values
                .Where(e => repoIds.Contains(e.RepoId, StringComparer.OrdinalIgnoreCase))
                .Where(e => query.IncludeExpired || e.Validity.ValidUntil is null)
                .Where(e => terms.Length == 0 ||
                            terms.Any(t => e.Content.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Take(query.Limit * 2)
                .ToList();
            return Task.FromResult(results);
        }
    }

    public Task<List<MemoryEntry>> VectorSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>());

    public Task<MemoryEntry?> FindDuplicateAsync(
        string repoId, string content, float threshold, CancellationToken ct = default) =>
        Task.FromResult<MemoryEntry?>(null);

    public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var counts = _entries.Values
                .Where(e => string.Equals(e.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                .Where(e => e.IsLatest && e.Validity.ValidUntil is null)
                .GroupBy(e => e.Type)
                .ToDictionary(g => g.Key, g => g.Count());
            return Task.FromResult(counts);
        }
    }

    public Task<List<MemoryEntry>> GetTopScoredAsync(
        string repoId, MemoryType[] types, int limit, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var results = _entries.Values
                .Where(e => string.Equals(e.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                .Where(e => types.Contains(e.Type))
                .Where(e => e.IsLatest && e.Validity.ValidUntil is null)
                .OrderByDescending(e => e.Importance)
                .Take(limit)
                .ToList();
            return Task.FromResult(results);
        }
    }

    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default) => Task.FromResult<DatabaseInfo?>(null);
    public Task EnsureIndexesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_entries.Values.Select(e => e.RepoId).Distinct().ToList());
    }

    public Task<List<MemoryEntry>> BrowseAsync(
        string repoId, int skip, int take, MemoryType? type = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var q = _entries.Values
                .Where(e => string.Equals(e.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                .Where(e => e.Validity.ValidUntil is null);
            if (type.HasValue) q = q.Where(e => e.Type == type.Value);
            return Task.FromResult(q.Skip(skip).Take(take).ToList());
        }
    }

    // ── Layers: enough to fan a cross-repo recall across ApplicableRepos ──
    public Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default)
    {
        lock (_lock) _layers[layer.Id] = layer;
        return Task.FromResult(layer.Id);
    }

    public Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_layers.Remove(layerId));
    }

    public Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            // "" means "all layers" (matches the real store's universal-scope query); a concrete
            // repo returns the layers whose ApplicableRepos include it.
            var layers = _layers.Values
                .Where(l => repoId.Length == 0 ||
                            l.ApplicableRepos.Contains(repoId, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return Task.FromResult(layers);
        }
    }

    public Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _layers.TryGetValue(layerId, out var l);
            return Task.FromResult(l);
        }
    }

    public Task<List<MemoryEntry>> GetByLayerIdAsync(string layerId, CancellationToken ct = default) =>
        Task.FromResult(new List<MemoryEntry>());

    public Task<bool> HardDeleteAsync(string id, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_entries.Remove(id));
    }
}
