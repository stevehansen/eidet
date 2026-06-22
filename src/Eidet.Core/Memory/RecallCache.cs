using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Eidet.Core.Domain;

namespace Eidet.Core.Memory;

/// <summary>
/// Bounded TTL cache for recall results, with per-scope generation tokens that prevent
/// stale-cache writes under concurrent store + recall. Mutations bump the scope's
/// generation; recall reads the generation at TryGet and passes it to Set, which drops
/// the write if any tracked generation has moved during the query. Lock-free on the
/// hot path.
/// </summary>
internal sealed class RecallCache
{
    private const int DefaultMaxEntries = 100;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;

    public RecallCache(int maxEntries = DefaultMaxEntries, TimeSpan? ttl = null)
    {
        _maxEntries = maxEntries;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>
    /// Read the cache for <paramref name="key"/> and snapshot the generations of
    /// <paramref name="scopes"/>. On miss, the snapshot is the value the caller passes
    /// to <see cref="Set"/> after running the query — Set drops if any scope's generation
    /// moved in the meantime.
    /// </summary>
    public bool TryGet(
        string key,
        IReadOnlyList<string> scopes,
        out ScopeGenerations observedGenerations,
        out List<MemorySearchResult> results)
    {
        observedGenerations = SnapshotGenerations(scopes);

        if (_entries.TryGetValue(key, out var entry) && !entry.IsExpired(_ttl)
            && entry.Generations.Matches(observedGenerations))
        {
            results = entry.Results;
            return true;
        }
        results = [];
        return false;
    }

    /// <summary>
    /// Write recall results for <paramref name="key"/>. Drops the write if any of
    /// <paramref name="observedGenerations"/> no longer matches the current generation —
    /// which means a mutation landed during the query and the results may be stale.
    /// </summary>
    public void Set(string key, ScopeGenerations observedGenerations, List<MemorySearchResult> results)
    {
        foreach (var (scope, observed) in observedGenerations.Pairs)
        {
            if (_generations.GetOrAdd(scope, 0) != observed) return;
        }
        EvictIfNeeded();
        _entries[key] = new Entry(results, observedGenerations);
    }

    /// <summary>
    /// Bump the generation for <paramref name="scope"/>, dropping its cached recalls.
    /// Null/empty scopes are ignored: the backing <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// rejects null keys, and there is no such scope to invalidate — so a malformed entry with
    /// no RepoId can't crash a bulk/background caller. Lock-free, fire-and-forget.
    /// </summary>
    public void Invalidate(string scope)
    {
        if (string.IsNullOrEmpty(scope)) return;
        _generations.AddOrUpdate(scope, 1, (_, g) => g + 1);
    }

    /// <summary>Bump the generation for every scope in <paramref name="scopes"/>.</summary>
    public void InvalidateAll(IEnumerable<string> scopes)
    {
        foreach (var scope in scopes) Invalidate(scope);
    }

    public static string ComputeKey(string repoId, MemoryQuery query, double alphaBucket)
    {
        var raw = $"{repoId}|{query.Text}|{query.Type}|{string.Join(",", query.Tags)}|{query.Limit}|{query.IncludeExpired}|{query.CrossRepo}|{alphaBucket}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }

    private ScopeGenerations SnapshotGenerations(IReadOnlyList<string> scopes)
    {
        var pairs = new (string Scope, long Generation)[scopes.Count];
        for (var i = 0; i < scopes.Count; i++)
            pairs[i] = (scopes[i], _generations.GetOrAdd(scopes[i], 0));
        return new ScopeGenerations(pairs);
    }

    private void EvictIfNeeded()
    {
        if (_entries.Count < _maxEntries) return;

        foreach (var kv in _entries)
        {
            if (kv.Value.IsExpired(_ttl))
                _entries.TryRemove(kv.Key, out _);
        }

        if (_entries.Count >= _maxEntries)
        {
            var overflow = _entries.Count - _maxEntries + 10;
            var oldest = _entries.OrderBy(kv => kv.Value.CreatedAt).Take(overflow).Select(kv => kv.Key).ToList();
            foreach (var key in oldest)
                _entries.TryRemove(key, out _);
        }
    }

    private sealed class Entry(List<MemorySearchResult> results, ScopeGenerations generations)
    {
        public List<MemorySearchResult> Results { get; } = results;
        public ScopeGenerations Generations { get; } = generations;
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public bool IsExpired(TimeSpan ttl) => DateTime.UtcNow - CreatedAt > ttl;
    }
}

/// <summary>Snapshot of (scope, generation) pairs taken at the start of a recall query.</summary>
internal readonly struct ScopeGenerations(IReadOnlyList<(string Scope, long Generation)> pairs)
{
    public IReadOnlyList<(string Scope, long Generation)> Pairs { get; } = pairs;

    public bool Matches(ScopeGenerations other)
    {
        if (Pairs.Count != other.Pairs.Count) return false;
        for (var i = 0; i < Pairs.Count; i++)
        {
            if (!string.Equals(Pairs[i].Scope, other.Pairs[i].Scope, StringComparison.OrdinalIgnoreCase)) return false;
            if (Pairs[i].Generation != other.Pairs[i].Generation) return false;
        }
        return true;
    }
}
