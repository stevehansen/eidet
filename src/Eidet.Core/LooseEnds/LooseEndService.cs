using System.Text;
using Eidet.Core.Domain;
using Eidet.Core.Gates;
using Eidet.Core.Memory;

namespace Eidet.Core.LooseEnds;

/// <summary>
/// The deep facade over the three Loose End seams (<see cref="ILooseEndStore"/>,
/// <see cref="IPromotionPort"/>, <see cref="TimeProvider"/>). Owns park (secret-scan only, signal
/// gate skipped), idempotent resolve, the one-call promote bridge, and the deterministic wake-up
/// slice + recall ride-along + pull surfaces. The promote path is the only one that re-enters the
/// gated memory write funnel, and it does so exclusively through <see cref="IPromotionPort"/>.
/// </summary>
public sealed class LooseEndService
{
    // Wake-up slice render policy (v1): hard item cap and the distinct open-work prefix.
    private const int WakeupItemCap = 3;
    private const string WakeupPrefix = "[~] ";

    private readonly ILooseEndStore _store;
    private readonly IPromotionPort _promote;
    private readonly TimeProvider _clock;

    public LooseEndService(ILooseEndStore store, IPromotionPort promote, TimeProvider clock)
    {
        _store = store;
        _promote = promote;
        _clock = clock;
    }

    // ─── Park ─────────────────────────────────────────────────────────

    /// <summary>80% surface — drop a terse note in one call.</summary>
    public Task<ParkResult> ParkAsync(string repoId, string note, CancellationToken ct = default) =>
        ParkAsync(new ParkOptions(repoId, note), ct);

    /// <summary>20% surface — tags, priority, alternate source.</summary>
    public async Task<ParkResult> ParkAsync(ParkOptions opts, CancellationToken ct = default)
    {
        // Park bypasses the signal gate by design (terse, speculative notes are the point) but
        // secret scanning is always-on for every write surface in Eidet.
        var secret = SecretScanRule.Check(opts.Note);
        if (!secret.Passed)
            return ParkResult.Rejected(secret.Reason ?? "rejected");

        var repoId = RepoIdNormalizer.Normalize(opts.RepoId);
        var now = _clock.GetUtcNow();
        var end = new LooseEnd
        {
            Id = LooseEndIdGenerator.Generate(repoId, opts.Note, now),
            RepoId = repoId,
            Note = opts.Note,
            Tags = opts.Tags?.ToList() ?? [],
            Priority = opts.Priority,
            Source = opts.Source,
            CreatedAt = now,
        };

        var id = await _store.StoreAsync(end, ct);
        return ParkResult.Parked(id);
    }

    // ─── Resolve ──────────────────────────────────────────────────────

    /// <summary>
    /// Close a Loose End with a typed resolution kind. Idempotent: re-resolving an already-resolved
    /// end is a no-op that returns its current state (it never re-mints on a second promote).
    /// </summary>
    public async Task<ResolveResult> ResolveAsync(
        string id, ResolutionKind kind, ResolveOptions? o = null, CancellationToken ct = default)
    {
        var end = await _store.GetAsync(id, ct);
        if (end is null)
            return ResolveResult.NotFound(id);

        if (end.State == LooseEndState.Resolved)
            return ResolveResult.From(end); // idempotent no-op

        var opts = o ?? new ResolveOptions();

        if (kind == ResolutionKind.Promoted)
        {
            var promotion = await _promote.PromoteAsync(
                end, new PromoteOptions(opts.PromoteType, opts.PromoteImportance, opts.ExternalRef), ct);
            if (!promotion.Success)
                return ResolveResult.Rejected(id, promotion.Reason ?? "promotion rejected");

            end.PromotedToMemoryId = promotion.MemoryId;
            end.ExternalRef = promotion.ExternalRef;
        }

        end.State = LooseEndState.Resolved;
        end.Resolution = kind;
        end.ResolutionNote = opts.Note;
        end.ResolvedAt = _clock.GetUtcNow();
        await _store.UpdateAsync(end, ct);

        return ResolveResult.From(end);
    }

    // ─── Surfacing ──────────────────────────────────────────────────────

    /// <summary>
    /// Pure, deterministic render of the open-work wake-up slice: cap 3, ordered by Priority
    /// (1=high first) then CreatedAt asc (stalest high-priority first), each line prefixed <c>[~] </c>, never spending
    /// more than <paramref name="maxTokens"/>. Returns "" when there are no open Loose Ends.
    /// </summary>
    public async Task<string> RenderWakeupSliceAsync(string repoId, int maxTokens, CancellationToken ct = default)
    {
        if (maxTokens <= 0) return "";

        var normalized = RepoIdNormalizer.Normalize(repoId);
        var open = await _store.ListOpenAsync(normalized, WakeupItemCap, ct);
        return RenderSlice(open, maxTokens);
    }

    /// <summary>Open Loose Ends whose tags overlap the recall query — the recall ride-along surface.</summary>
    public Task<IReadOnlyList<LooseEnd>> RideAlongAsync(
        string repoId, IReadOnlyList<string> recallTags, CancellationToken ct = default)
    {
        if (recallTags.Count == 0)
            return Task.FromResult<IReadOnlyList<LooseEnd>>([]);
        return _store.FindOpenByTagsAsync(RepoIdNormalizer.Normalize(repoId), recallTags, WakeupItemCap, ct);
    }

    /// <summary>Explicit open-work pull list (human REST/UI surface).</summary>
    public Task<IReadOnlyList<LooseEnd>> PullAsync(string repoId, int max = 20, CancellationToken ct = default) =>
        _store.ListOpenAsync(RepoIdNormalizer.Normalize(repoId), max, ct);

    /// <summary>Open-work count for the wake-up L0 addendum — avoids materializing the open set.</summary>
    public Task<int> CountOpenAsync(string repoId, CancellationToken ct = default) =>
        _store.CountOpenAsync(RepoIdNormalizer.Normalize(repoId), ct);

    // `open` arrives already ordered (Priority→CreatedAt) and capped at WakeupItemCap by the store —
    // ordering is owned there so it stays in one place; this is pure token-budgeted rendering.
    private static string RenderSlice(IReadOnlyList<LooseEnd> open, int maxTokens)
    {
        if (open.Count == 0) return "";

        var sb = new StringBuilder();
        var remaining = maxTokens;
        foreach (var end in open)
        {
            var line = WakeupPrefix + end.Note;
            var lineTokens = RecallScoring.EstimateTokens(line.Length);
            if (lineTokens > remaining) break;
            sb.AppendLine(line);
            remaining -= lineTokens;
        }
        return sb.ToString();
    }
}

/// <summary>20% surface for <see cref="LooseEndService.ParkAsync(ParkOptions, CancellationToken)"/>.</summary>
public sealed record ParkOptions(string RepoId, string Note)
{
    public IReadOnlyList<string>? Tags { get; init; }
    public int Priority { get; init; } = 2;
    public string Source { get; init; } = "claude-session";
}

/// <summary>Options for <see cref="LooseEndService.ResolveAsync"/>. Promote fields honored only when kind == Promoted.</summary>
public sealed record ResolveOptions
{
    public string? Note { get; init; }
    public MemoryType PromoteType { get; init; } = MemoryType.Insight;
    public float PromoteImportance { get; init; } = 0.5f;
    public string? ExternalRef { get; init; }
}

/// <summary>Outcome of a park attempt: the parked id, or the rejection reason.</summary>
public sealed record ParkResult(bool Success, string? Id, string? Reason)
{
    public static ParkResult Parked(string id) => new(true, id, null);
    public static ParkResult Rejected(string reason) => new(false, null, reason);
}

/// <summary>Outcome of a resolve: the closed state, plus the minted memory id when promoted.</summary>
public sealed record ResolveResult(
    bool Success, string Id, LooseEndState State,
    ResolutionKind? Kind = null, string? PromotedToMemoryId = null, string? ExternalRef = null, string? Reason = null)
{
    public static ResolveResult From(LooseEnd e) =>
        new(true, e.Id, e.State, e.Resolution, e.PromotedToMemoryId, e.ExternalRef);
    public static ResolveResult NotFound(string id) =>
        new(false, id, LooseEndState.Open, Reason: "not found");
    public static ResolveResult Rejected(string id, string reason) =>
        new(false, id, LooseEndState.Open, Reason: reason);
}
