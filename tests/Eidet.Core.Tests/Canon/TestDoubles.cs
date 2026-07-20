using System.Runtime.CompilerServices;
using Eidet.Core.Canon;
using Eidet.Core.Domain;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Canon;

/// <summary>
/// Minimal in-memory <see cref="ICanonDraftStore"/> for Canon tests. Mirrors the lock+dictionary style
/// of <c>InMemoryLooseEndStore</c> / <c>InMemoryEidetStore</c>. <see cref="ListAsync"/> returns
/// newest-proposed first, exactly as the Raven adapter's index does, so <c>ListPendingAsync</c> ordering
/// is testable here.
/// </summary>
internal sealed class InMemoryCanonDraftStore : ICanonDraftStore
{
    private readonly Dictionary<string, CanonDraft> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) return _drafts.Count; }
    }

    public Task<string> StoreAsync(CanonDraft d, CancellationToken ct = default)
    {
        lock (_lock) _drafts[d.Id] = d;
        return Task.FromResult(d.Id);
    }

    public Task<CanonDraft?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _drafts.TryGetValue(id, out var d);
            return Task.FromResult(d);
        }
    }

    public Task UpdateAsync(CanonDraft d, CancellationToken ct = default)
    {
        lock (_lock) _drafts[d.Id] = d;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CanonDraft>> ListAsync(
        string repoId, CanonDraftStatus? status, int max, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var q = _drafts.Values
                .Where(d => string.Equals(d.RepoId, repoId, StringComparison.OrdinalIgnoreCase))
                .Where(d => status is null || d.Status == status)
                .OrderByDescending(d => d.ProposedAt)   // newest-proposed first
                .Take(max)
                .ToList();
            return Task.FromResult<IReadOnlyList<CanonDraft>>(q);
        }
    }

    public Task<CanonDraft?> FindBySlugAsync(
        string repoId, CanonKind kind, string slug, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var d = _drafts.Values.FirstOrDefault(x =>
                string.Equals(x.RepoId, repoId, StringComparison.OrdinalIgnoreCase) &&
                x.Kind == kind &&
                string.Equals(x.Slug, slug, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(d);
        }
    }

    /// <summary>
    /// Genuinely atomic Pending→Approving under the same lock as every other mutation — the in-memory
    /// twin of the Raven adapter's change-vector CAS. This OVERRIDES the interface's default non-atomic
    /// read-check-write so the concurrency test sees exactly one winner even under real parallelism.
    /// </summary>
    public Task<bool> TryClaimForApproveAsync(string id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_drafts.TryGetValue(id, out var d) || d.Status != CanonDraftStatus.Pending)
                return Task.FromResult(false);
            d.Status = CanonDraftStatus.Approving;
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// Delegating <see cref="ICanonDraftStore"/> whose first N claim attempts fail while the draft stays
/// Pending — the "a peer claimed then released" shape that the bounded approve retry must absorb
/// instead of falsely reporting the draft in-progress.
/// </summary>
internal sealed class FlakyClaimCanonDraftStore : ICanonDraftStore
{
    private readonly InMemoryCanonDraftStore _inner = new();
    private int _failuresRemaining;

    public FlakyClaimCanonDraftStore(int failures) => _failuresRemaining = failures;

    public int ClaimAttempts { get; private set; }

    public Task<string> StoreAsync(CanonDraft d, CancellationToken ct = default) => _inner.StoreAsync(d, ct);
    public Task<CanonDraft?> GetAsync(string id, CancellationToken ct = default) => _inner.GetAsync(id, ct);
    public Task UpdateAsync(CanonDraft d, CancellationToken ct = default) => _inner.UpdateAsync(d, ct);

    public Task<IReadOnlyList<CanonDraft>> ListAsync(
        string repoId, CanonDraftStatus? status, int max, CancellationToken ct = default) =>
        _inner.ListAsync(repoId, status, max, ct);

    public Task<CanonDraft?> FindBySlugAsync(
        string repoId, CanonKind kind, string slug, CancellationToken ct = default) =>
        _inner.FindBySlugAsync(repoId, kind, slug, ct);

    public Task<bool> TryClaimForApproveAsync(string id, CancellationToken ct = default)
    {
        ClaimAttempts++;
        if (_failuresRemaining > 0)
        {
            _failuresRemaining--;
            return Task.FromResult(false);   // lost the CAS; the doc is still Pending
        }
        return _inner.TryClaimForApproveAsync(id, ct);
    }
}

/// <summary>A single recorded mint — a snapshot of the draft fields the adapter would turn into the
/// minted memory's payload, captured at mint time so later draft mutations can't change what was asserted.</summary>
internal sealed record CanonMintRecord(
    string DraftId, string Slug, CanonKind Kind, IReadOnlyList<string> MemberIds,
    string? SupersedesCanonId, string? EditedContent, string MemoryId);

/// <summary>
/// Recording <see cref="ICanonMintPort"/> test double: captures every mint and returns a distinct,
/// deterministic memory id per call so a re-approve's <c>Supersedes</c> chain can be asserted against the
/// prior mint's id. Never fails — mint-gate rejection is exercised through the REAL adapter in the gate
/// integration test, not here.
/// </summary>
internal sealed class RecordingCanonMintPort : ICanonMintPort
{
    private int _counter;
    public List<CanonMintRecord> Mints { get; } = [];
    public CanonMintRecord LastMint => Mints[^1];
    public int CallCount => Mints.Count;

    public Task<CanonMintResult> MintAsync(CanonDraft draft, string? editedContent, CancellationToken ct = default)
    {
        var memoryId = $"memories/{draft.RepoId}/insight/canon-{++_counter}";
        Mints.Add(new CanonMintRecord(
            draft.Id, draft.Slug, draft.Kind, draft.MemberIds.ToList(),
            draft.SupersedesCanonId, editedContent, memoryId));
        return Task.FromResult(new CanonMintResult(true, memoryId, null));
    }
}

/// <summary>
/// Gated <see cref="ICanonMintPort"/> for the deterministic double-mint test. The first caller into
/// <see cref="MintAsync"/> signals <see cref="Entered"/> then suspends until <see cref="Release"/>, so a
/// second approver can run its claim while the first is still mid-mint — forcing the loser's claim to lose
/// against the in-flight <c>Approving</c> doc without timing races (the LooseEnd <c>GatedPromotionAdapter</c> twin).
/// </summary>
internal sealed class GatedCanonMintPort : ICanonMintPort
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CallCount;
    public string MemoryId { get; set; } = "memories/repo-a/insight/canon-1";

    public Task Entered => _entered.Task;
    public void Release() => _gate.TrySetResult();

    public async Task<CanonMintResult> MintAsync(CanonDraft draft, string? editedContent, CancellationToken ct = default)
    {
        Interlocked.Increment(ref CallCount);
        _entered.TrySetResult();
        await _gate.Task;
        return new CanonMintResult(true, MemoryId, null);
    }
}

/// <summary>
/// Scripted <see cref="ICanonDraftSource"/> returning a fixed candidate list the test controls. Mutate
/// <see cref="Candidates"/> between <c>RegenerateDraftsAsync</c> calls to drive drift/idempotency; toggle
/// <see cref="Applies"/> to exercise the <c>AppliesTo</c> gate.
/// </summary>
internal sealed class ScriptedCanonDraftSource : ICanonDraftSource
{
    public ScriptedCanonDraftSource(string name = "scripted") => Name = name;

    public string Name { get; }
    public bool Applies { get; set; } = true;
    public List<CanonDraftCandidate> Candidates { get; set; } = [];

    public bool AppliesTo(CanonProposalContext ctx) => Applies;

    public async IAsyncEnumerable<CanonDraftCandidate> ProposeAsync(
        CanonProposalContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var c in Candidates)
        {
            ct.ThrowIfCancellationRequested();
            yield return c;
        }
    }
}

/// <summary>Builds Canon draft candidates with a realistic fingerprint (<see cref="CanonFingerprint.Of"/>)
/// so identical inputs collide (idempotency) and any content/member change diverges (drift).</summary>
internal static class CanonCandidates
{
    public static CanonDraftCandidate Term(string slug, string title, string content, params string[] members)
    {
        var memberIds = members.ToList();
        return new CanonDraftCandidate(
            CanonKind.Term, slug, title, content, memberIds,
            CanonFingerprint.Of(CanonKind.Term, title, content, memberIds));
    }
}

/// <summary>
/// In-memory <see cref="Eidet.Core.Storage.IEidetStore"/> whose <c>GetAsync</c> THROWS for one configured
/// id — drives the citation-hydration catch arm (a member read that faults degrades to a placeholder,
/// never a throw), distinct from the not-found (null) arm.
/// </summary>
internal sealed class ThrowingGetEidetStore : InMemoryEidetStore
{
    private readonly string _throwId;
    public ThrowingGetEidetStore(string throwId) => _throwId = throwId;

    public override Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) =>
        string.Equals(id, _throwId, StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException("simulated member read failure")
            : base.GetAsync(id, ct);
}

/// <summary>
/// Hand-rolled deterministic <see cref="TimeProvider"/> (no Microsoft.Extensions.TimeProvider.Testing
/// dependency), matching the LooseEnds test style. Drives rejection-cooldown determinism.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public FakeTimeProvider(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
