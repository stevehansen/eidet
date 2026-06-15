using Eidet.Core.LooseEnds;

namespace Eidet.Core.Tests.LooseEnds;

/// <summary>
/// Minimal in-memory <see cref="ILooseEndStore"/> for Loose End tests. Mirrors the lock+dictionary
/// style of <c>InMemoryEidetStore</c>. Ordering (Priority asc = high first, then CreatedAt asc) is
/// applied on the open-view reads exactly as the Raven adapter does, so wake-up/pull ordering is testable here.
/// </summary>
internal sealed class InMemoryLooseEndStore : ILooseEndStore
{
    private readonly Dictionary<string, LooseEnd> _ends = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) return _ends.Count; }
    }

    public Task<string> StoreAsync(LooseEnd e, CancellationToken ct = default)
    {
        lock (_lock) _ends[e.Id] = e;
        return Task.FromResult(e.Id);
    }

    public Task<LooseEnd?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _ends.TryGetValue(id, out var e);
            return Task.FromResult(e);
        }
    }

    public Task UpdateAsync(LooseEnd e, CancellationToken ct = default)
    {
        lock (_lock) _ends[e.Id] = e;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LooseEnd>> ListOpenAsync(string repoId, int max, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var open = _ends.Values
                .Where(e => string.Equals(e.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                .Where(e => e.State == LooseEndState.Open);
            return Task.FromResult<IReadOnlyList<LooseEnd>>(Order(open).Take(max).ToList());
        }
    }

    public Task<IReadOnlyList<LooseEnd>> FindOpenByTagsAsync(
        string repoId, IReadOnlyList<string> tags, int max, CancellationToken ct = default)
    {
        if (tags.Count == 0) return Task.FromResult<IReadOnlyList<LooseEnd>>([]);
        lock (_lock)
        {
            var wanted = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
            var matched = _ends.Values
                .Where(e => string.Equals(e.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                .Where(e => e.State == LooseEndState.Open)
                .Where(e => e.Tags.Any(wanted.Contains));
            return Task.FromResult<IReadOnlyList<LooseEnd>>(Order(matched).Take(max).ToList());
        }
    }

    public Task<int> CountOpenAsync(string repoId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var count = _ends.Values.Count(e =>
                string.Equals(e.RepoId, repoId, StringComparison.OrdinalIgnoreCase) &&
                e.State == LooseEndState.Open);
            return Task.FromResult(count);
        }
    }

    private static IEnumerable<LooseEnd> Order(IEnumerable<LooseEnd> ends) =>
        ends.OrderBy(e => e.Priority).ThenBy(e => e.CreatedAt);
}

/// <summary>
/// Recording <see cref="IPromotionPort"/> test double. Captures the last promote call and returns a
/// configurable <see cref="PromotionResult"/> (default: success minting a fake memory id) so the
/// service-level promote wiring is testable without a real <c>MemoryService</c>.
/// </summary>
internal sealed class InMemoryPromotionAdapter : IPromotionPort
{
    public PromotionResult Next { get; set; } = new(true, "memories/fake/insight/abc123", null, null);
    public LooseEnd? LastEnd { get; private set; }
    public PromoteOptions? LastOptions { get; private set; }
    public int CallCount { get; private set; }

    public Task<PromotionResult> PromoteAsync(LooseEnd e, PromoteOptions opts, CancellationToken ct = default)
    {
        CallCount++;
        LastEnd = e;
        LastOptions = opts;
        return Task.FromResult(Next);
    }
}

/// <summary>
/// Hand-rolled deterministic <see cref="TimeProvider"/> (no Microsoft.Extensions.TimeProvider.Testing
/// dependency). Drives deterministic Loose End IDs and CreatedAt ordering. <see cref="Advance"/>
/// moves the clock forward so parks get strictly increasing timestamps.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
