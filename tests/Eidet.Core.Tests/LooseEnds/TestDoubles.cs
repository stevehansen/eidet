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

    public Task<bool> TryClaimForResolveAsync(string id, CancellationToken ct = default)
    {
        // Genuinely atomic check-and-set under the same lock as every other mutation, so concurrent
        // claims serialize and exactly one observes Open and flips it to Resolving.
        lock (_lock)
        {
            if (!_ends.TryGetValue(id, out var e) || e.State != LooseEndState.Open)
                return Task.FromResult(false);
            e.State = LooseEndState.Resolving;
            return Task.FromResult(true);
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
/// Gated <see cref="IPromotionPort"/> test double for deterministic concurrency tests. The first
/// caller into <see cref="PromoteAsync"/> signals <see cref="Entered"/> then suspends until the test
/// releases <see cref="Gate"/>, so a second resolver can run its claim while the first is still
/// mid-promote — reproducing the FM1 double-mint window without timing races.
/// </summary>
internal sealed class GatedPromotionAdapter : IPromotionPort
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PromotionResult Next { get; set; } = new(true, "memories/fake/insight/abc123", null, null);
    public int CallCount;

    /// <summary>Completes once a caller has entered <see cref="PromoteAsync"/> and is parked on the gate.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Release the suspended promote so it can finish.</summary>
    public void Release() => _gate.TrySetResult();

    public async Task<PromotionResult> PromoteAsync(LooseEnd e, PromoteOptions opts, CancellationToken ct = default)
    {
        Interlocked.Increment(ref CallCount);
        _entered.TrySetResult();
        await _gate.Task;
        return Next;
    }
}

/// <summary>Promotion port that always throws — drives the promote-throws release path.</summary>
internal sealed class ThrowingPromotionAdapter(Exception toThrow) : IPromotionPort
{
    public Task<PromotionResult> PromoteAsync(LooseEnd e, PromoteOptions opts, CancellationToken ct = default) =>
        throw toThrow;
}

/// <summary>
/// Wraps an inner store and HONORS the cancellation token on <see cref="UpdateAsync"/> (throws if the
/// token is cancelled before delegating), like a real RavenDB session. Proves the claim-release runs
/// even when the caller's token is already cancelled — i.e. that release uses CancellationToken.None.
/// </summary>
internal sealed class CancellationHonoringStore(ILooseEndStore inner) : ILooseEndStore
{
    public Task UpdateAsync(LooseEnd e, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return inner.UpdateAsync(e, ct);
    }

    public Task<string> StoreAsync(LooseEnd e, CancellationToken ct = default) => inner.StoreAsync(e, ct);
    public Task<LooseEnd?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);
    public Task<bool> TryClaimForResolveAsync(string id, CancellationToken ct = default) => inner.TryClaimForResolveAsync(id, ct);
    public Task<IReadOnlyList<LooseEnd>> ListOpenAsync(string repoId, int max, CancellationToken ct = default) => inner.ListOpenAsync(repoId, max, ct);
    public Task<IReadOnlyList<LooseEnd>> FindOpenByTagsAsync(string repoId, IReadOnlyList<string> tags, int max, CancellationToken ct = default) => inner.FindOpenByTagsAsync(repoId, tags, max, ct);
    public Task<int> CountOpenAsync(string repoId, CancellationToken ct = default) => inner.CountOpenAsync(repoId, ct);
}

/// <summary>
/// Wraps an inner store and forces the FIRST <see cref="TryClaimForResolveAsync"/> to lose (returns
/// false without changing state), delegating afterward — simulates a peer that won the claim then
/// released the end back to Open, so the service's bounded retry should re-claim and resolve.
/// </summary>
internal sealed class ClaimFailsOnceStore(ILooseEndStore inner) : ILooseEndStore
{
    private int _claimCalls;

    public Task<bool> TryClaimForResolveAsync(string id, CancellationToken ct = default) =>
        ++_claimCalls == 1 ? Task.FromResult(false) : inner.TryClaimForResolveAsync(id, ct);

    public Task<string> StoreAsync(LooseEnd e, CancellationToken ct = default) => inner.StoreAsync(e, ct);
    public Task<LooseEnd?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);
    public Task UpdateAsync(LooseEnd e, CancellationToken ct = default) => inner.UpdateAsync(e, ct);
    public Task<IReadOnlyList<LooseEnd>> ListOpenAsync(string repoId, int max, CancellationToken ct = default) => inner.ListOpenAsync(repoId, max, ct);
    public Task<IReadOnlyList<LooseEnd>> FindOpenByTagsAsync(string repoId, IReadOnlyList<string> tags, int max, CancellationToken ct = default) => inner.FindOpenByTagsAsync(repoId, tags, max, ct);
    public Task<int> CountOpenAsync(string repoId, CancellationToken ct = default) => inner.CountOpenAsync(repoId, ct);
}

/// <summary>
/// Wraps an inner <see cref="ILooseEndStore"/> and throws on the Nth <see cref="UpdateAsync"/> call
/// (1-based), delegating everything else (including the atomic claim). Exercises the partial-failure
/// path where the final resolve write throws AFTER a successful promote — the claim-release must then
/// still leave a clean Open end. Throwing on call 1 (the final write) lets the release write (call 2) succeed.
/// </summary>
internal sealed class ThrowOnNthUpdateStore(ILooseEndStore inner, int throwOnCall) : ILooseEndStore
{
    private int _updateCalls;

    public Task<string> StoreAsync(LooseEnd e, CancellationToken ct = default) => inner.StoreAsync(e, ct);
    public Task<LooseEnd?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);

    public Task UpdateAsync(LooseEnd e, CancellationToken ct = default)
    {
        if (++_updateCalls == throwOnCall)
            throw new InvalidOperationException("simulated store write failure");
        return inner.UpdateAsync(e, ct);
    }

    public Task<bool> TryClaimForResolveAsync(string id, CancellationToken ct = default) =>
        inner.TryClaimForResolveAsync(id, ct);
    public Task<IReadOnlyList<LooseEnd>> ListOpenAsync(string repoId, int max, CancellationToken ct = default) =>
        inner.ListOpenAsync(repoId, max, ct);
    public Task<IReadOnlyList<LooseEnd>> FindOpenByTagsAsync(string repoId, IReadOnlyList<string> tags, int max, CancellationToken ct = default) =>
        inner.FindOpenByTagsAsync(repoId, tags, max, ct);
    public Task<int> CountOpenAsync(string repoId, CancellationToken ct = default) =>
        inner.CountOpenAsync(repoId, ct);
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
