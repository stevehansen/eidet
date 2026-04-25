using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Eidet.Core.Domain;

namespace Eidet.Core.Memory;

/// <summary>
/// Bounded TTL cache for recall results. Keyed by a deterministic hash of repo + query.
/// Writes invalidate the whole cache; eviction trims expired entries first, then oldest.
/// </summary>
internal sealed class RecallCache
{
    private const int DefaultMaxEntries = 100;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;

    public RecallCache(int maxEntries = DefaultMaxEntries, TimeSpan? ttl = null)
    {
        _maxEntries = maxEntries;
        _ttl = ttl ?? DefaultTtl;
    }

    public bool TryGet(string key, out List<MemorySearchResult> results)
    {
        if (_entries.TryGetValue(key, out var entry) && !entry.IsExpired(_ttl))
        {
            results = entry.Results;
            return true;
        }
        results = [];
        return false;
    }

    public void Set(string key, List<MemorySearchResult> results)
    {
        EvictIfNeeded();
        _entries[key] = new Entry(results);
    }

    public void Invalidate() => _entries.Clear();

    public static string ComputeKey(string repoId, MemoryQuery query)
    {
        var raw = $"{repoId}|{query.Text}|{query.Type}|{string.Join(",", query.Tags)}|{query.Limit}|{query.IncludeExpired}|{query.CrossRepo}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
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

    private sealed class Entry(List<MemorySearchResult> results)
    {
        public List<MemorySearchResult> Results { get; } = results;
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public bool IsExpired(TimeSpan ttl) => DateTime.UtcNow - CreatedAt > ttl;
    }
}
