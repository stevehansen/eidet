using Eidet.Core.Domain;
using Eidet.Core.Gates;
using Eidet.Core.Storage;

namespace Eidet.Core.Canon;

/// <summary>
/// The deep facade over the Canon seams (<see cref="ICanonDraftStore"/>, <see cref="ICanonMintPort"/>,
/// the <see cref="ICanonDraftSource"/> set, and <see cref="TimeProvider"/>). It owns the whole reviewer
/// loop — list/hydrate pending drafts, approve (claim → mint → finalize), reject with cooldown — plus
/// damped, idempotent regeneration and bulk approval. It hides fingerprinting, slug derivation, the
/// damper matrix, the double-mint claim protocol, citation hydration, and DTO projection.
///
/// It reads <see cref="IEidetStore"/> for ONE purpose: hydrating a draft's cited members into
/// <see cref="Citation"/>s at GET time. That is a reviewer-loop concern (the review UI renders
/// clickable citations), not a draft-source concern, so it lives here rather than behind a source port.
/// Approve is the sole write path into <c>memories/*</c>; it always routes through <see cref="ICanonMintPort"/>.
/// </summary>
public sealed class CanonService
{
    // Days a rejected draft is blocked from re-proposal; re-proposal also requires a fingerprint change.
    private const int RejectionCooldownDays = 7;
    private const int BulkApproveScanCap = 200;
    // A failed mint releases the draft to Pending, so a lost claim that re-reads as Pending is retried
    // rather than falsely reported in-progress. Bounded so a Pending↔Approving flip can never livelock.
    private const int MaxClaimAttempts = 3;

    private readonly ICanonDraftStore _drafts;
    private readonly ICanonMintPort _mint;
    private readonly IReadOnlyList<ICanonDraftSource> _sources;
    private readonly IEidetStore _store;
    private readonly TimeProvider _clock;

    public CanonService(
        ICanonDraftStore drafts, ICanonMintPort mint, IEnumerable<ICanonDraftSource> sources,
        IEidetStore store, TimeProvider clock)
    {
        _drafts = drafts;
        _mint = mint;
        _sources = sources.ToList();
        _store = store;
        _clock = clock;
    }

    // ─── Reviewer loop ────────────────────────────────────────────────

    /// <summary>The pending review queue for a repo, newest first — the 80% surface.</summary>
    public async Task<IReadOnlyList<CanonDraftSummary>> ListPendingAsync(
        string repoId, int max = 50, CancellationToken ct = default)
    {
        var pending = await _drafts.ListAsync(
            RepoIdNormalizer.Normalize(repoId), CanonDraftStatus.Pending, max, ct);
        return pending.Select(ToSummary).ToList();
    }

    /// <summary>
    /// One draft, hydrated with its cited members as clickable <see cref="Citation"/>s. A member that was
    /// forgotten or hard-deleted since the draft was proposed degrades to a placeholder citation — never a
    /// throw — so a deleted member can't 500 the review view. Returns null only when the draft is unknown.
    /// </summary>
    public async Task<CanonDraftDetail?> GetDraftAsync(string id, CancellationToken ct = default)
    {
        var d = await _drafts.GetAsync(id, ct);
        if (d is null) return null;

        var citations = await HydrateCitationsAsync(d.MemberIds, ct);
        return new CanonDraftDetail(ToSummary(d), d.ProposedContent, citations, d.Fingerprint, d.CooldownUntil);
    }

    /// <summary>
    /// Approve a draft into a <c>canon:*</c> memory. Claims the draft (Pending→Approving) BEFORE minting so
    /// two concurrent approves can never both mint; on mint failure the claim is released back to Pending.
    /// Idempotent: a second approve of an already-approved draft returns its existing minted id with success.
    /// </summary>
    public async Task<ApproveResult> ApproveAsync(
        string draftId, string? editedContent = null, CancellationToken ct = default)
    {
        // A lost claim re-reads to tell a finished peer (idempotent success) from a rejected/superseded
        // draft (distinct refusal) from one mid-flight (conflict) from one a peer claimed-then-released
        // back to Pending (retry, bounded — the LooseEnd resolve pattern).
        for (var attempt = 1; ; attempt++)
        {
            var d = await _drafts.GetAsync(draftId, ct);
            if (d is null) return ApproveResult.NotFound(draftId);
            if (d.Status == CanonDraftStatus.Approved)
                return new ApproveResult(true, draftId, d.MintedMemoryId, null); // idempotent no-op
            if (d.Status is CanonDraftStatus.Rejected or CanonDraftStatus.Superseded)
                return ApproveResult.NotPending(draftId, d.Status);

            if (await _drafts.TryClaimForApproveAsync(draftId, ct))
                return await CompleteClaimedApproveAsync(d, draftId, editedContent, ct);

            var after = await _drafts.GetAsync(draftId, ct);
            if (after is null) return ApproveResult.NotFound(draftId);
            if (after.Status == CanonDraftStatus.Approved)
                return new ApproveResult(true, draftId, after.MintedMemoryId, null); // a peer finished it
            if (after.Status is CanonDraftStatus.Rejected or CanonDraftStatus.Superseded)
                return ApproveResult.NotPending(draftId, after.Status);
            if (after.Status == CanonDraftStatus.Pending && attempt < MaxClaimAttempts)
                continue;                                    // a peer released it — claim it ourselves
            return ApproveResult.ClaimLost(draftId);         // a peer is mid-flight
        }
    }

    // Claim won: the store doc is now Approving, but local `d` is still Pending — so ReleaseToPending's
    // UpdateAsync(d) naturally restores the store to Pending on any failure below.
    private async Task<ApproveResult> CompleteClaimedApproveAsync(
        CanonDraft d, string draftId, string? editedContent, CancellationToken ct)
    {
        try
        {
            var mint = await _mint.MintAsync(d, editedContent, ct);
            if (!mint.Success)
            {
                await ReleaseToPendingAsync(d);
                return new ApproveResult(false, draftId, null, mint.Reason ?? "canon mint rejected");
            }

            d.Status = CanonDraftStatus.Approved;
            d.MintedMemoryId = mint.MemoryId;
            await _drafts.UpdateAsync(d, ct);
            return new ApproveResult(true, draftId, mint.MemoryId, null);
        }
        catch
        {
            // Mint threw, or the finalize write threw (including cancellation) — never leave a draft wedged
            // in Approving. ReleaseToPending runs even when `ct` is already cancelled.
            try { await ReleaseToPendingAsync(d); } catch { /* best-effort; store likely down */ }
            throw;
        }
    }

    /// <summary>Reject a draft with a reason and a cooldown; blocks re-proposal until the cooldown elapses
    /// AND the draft's fingerprint changes. A draft already approved cannot be rejected.</summary>
    public async Task<RejectResult> RejectAsync(string draftId, string reason, CancellationToken ct = default)
    {
        var d = await _drafts.GetAsync(draftId, ct);
        if (d is null) return RejectResult.NotFound(draftId);
        if (d.Status == CanonDraftStatus.Approved)
            return new RejectResult(false, draftId, null, "already approved");

        d.Status = CanonDraftStatus.Rejected;
        d.RejectReason = reason;
        d.CooldownUntil = _clock.GetUtcNow().AddDays(RejectionCooldownDays);
        await _drafts.UpdateAsync(d, ct);
        return new RejectResult(true, draftId, d.CooldownUntil, null);
    }

    // ─── Generation & bulk ────────────────────────────────────────────

    /// <summary>
    /// Run every applicable source, secret-scan each candidate, and apply the damper — the deterministic P1
    /// generator (P2's <c>CanonProposalStage</c> body is this call). Idempotent: a second run over an
    /// unchanged repo stores nothing. Returns the number of drafts created or refreshed.
    /// The single string is the repo's filesystem path: the RepoId is normalized from it, the ProjectPath
    /// is the path verbatim (so file-backed sources like UL.md resolve).
    /// </summary>
    public async Task<int> RegenerateDraftsAsync(string repoPath, CancellationToken ct = default)
    {
        var ctx = new CanonProposalContext(RepoIdNormalizer.Normalize(repoPath), repoPath);
        var now = _clock.GetUtcNow();
        var count = 0;

        foreach (var source in _sources.Where(s => s.AppliesTo(ctx)))
        {
            await foreach (var cand in source.ProposeAsync(ctx, ct).WithCancellation(ct))
            {
                // Defense in depth: a source's prose can echo a secret present in member content. The full
                // gate runs again at Approve, but drop the candidate now so it never sits in the queue.
                if (!WriteValidator.ScanSecrets(cand.ProposedContent).Passed) continue;

                var existing = await _drafts.FindBySlugAsync(ctx.RepoId, cand.Kind, cand.Slug, ct);
                if (await ApplyDamperAsync(ctx.RepoId, source.Name, cand, existing, now, ct))
                    count++;
            }
        }
        return count;
    }

    /// <summary>Approve every pending draft from one source (e.g. all UL-seeded terms) in one call.</summary>
    public async Task<BulkApproveResult> BulkApproveAsync(
        string repoId, string sourceName, CancellationToken ct = default)
    {
        var pending = await _drafts.ListAsync(
            RepoIdNormalizer.Normalize(repoId), CanonDraftStatus.Pending, BulkApproveScanCap, ct);

        var approved = 0;
        var failed = 0;
        foreach (var d in pending.Where(d => string.Equals(d.SourceName, sourceName, StringComparison.OrdinalIgnoreCase)))
        {
            var r = await ApproveAsync(d.Id, null, ct);
            if (r.Success) approved++;
            else failed++;
        }
        return new BulkApproveResult(approved, failed);
    }

    // ─── Damper matrix ────────────────────────────────────────────────

    // Decide what to do with one candidate against the draft (if any) already keyed by its slug. Returns
    // true when a draft was created or refreshed (i.e. the queue changed), false when nothing was done.
    private async Task<bool> ApplyDamperAsync(
        string repoId, string sourceName, CanonDraftCandidate cand, CanonDraft? existing,
        DateTimeOffset now, CancellationToken ct)
    {
        if (existing is null)
        {
            await _drafts.StoreAsync(NewDraft(repoId, sourceName, cand, now), ct);
            return true;
        }

        switch (existing.Status)
        {
            case CanonDraftStatus.Approving:
                // A claim is mid-flight — never disturb the document being minted.
                return false;

            case CanonDraftStatus.Pending:
                if (existing.Fingerprint == cand.Fingerprint) return false; // identical — no churn
                RefreshInPlace(existing, cand, now);
                await _drafts.UpdateAsync(existing, ct);
                return true;

            case CanonDraftStatus.Rejected:
                var cooldownElapsed = existing.CooldownUntil is null || now >= existing.CooldownUntil;
                if (!cooldownElapsed || existing.Fingerprint == cand.Fingerprint) return false;
                existing.RejectReason = null;
                existing.CooldownUntil = null;
                existing.SupersedesCanonId = null;
                RefreshInPlace(existing, cand, now); // reopens as Pending
                await _drafts.UpdateAsync(existing, ct);
                return true;

            case CanonDraftStatus.Approved:
                if (existing.Fingerprint == cand.Fingerprint) return false; // the approved page is still current
                // Queue a superseding draft over the live page: on next approve, mint with Supersedes set.
                existing.SupersedesCanonId = existing.MintedMemoryId;
                existing.MintedMemoryId = null;
                RefreshInPlace(existing, cand, now);
                await _drafts.UpdateAsync(existing, ct);
                return true;

            case CanonDraftStatus.Superseded:
            default:
                if (existing.Fingerprint == cand.Fingerprint) return false;
                RefreshInPlace(existing, cand, now);
                await _drafts.UpdateAsync(existing, ct);
                return true;
        }
    }

    private CanonDraft NewDraft(string repoId, string sourceName, CanonDraftCandidate cand, DateTimeOffset now) => new()
    {
        Id = CanonDraftId.For(repoId, cand.Kind, cand.Slug),
        RepoId = repoId,
        Kind = cand.Kind,
        Slug = cand.Slug,
        Title = cand.Title,
        ProposedContent = cand.ProposedContent,
        MemberIds = cand.MemberIds.ToList(),
        Fingerprint = cand.Fingerprint,
        ProposedAt = now,
        Status = CanonDraftStatus.Pending,
        SourceName = sourceName,
    };

    // Reset a drifted/reopened draft to a clean Pending state carrying the new candidate's render fields.
    private static void RefreshInPlace(CanonDraft d, CanonDraftCandidate cand, DateTimeOffset now)
    {
        d.Status = CanonDraftStatus.Pending;
        d.Title = cand.Title;
        d.ProposedContent = cand.ProposedContent;
        d.MemberIds = cand.MemberIds.ToList();
        d.Fingerprint = cand.Fingerprint;
        d.ProposedAt = now;
    }

    // Restore a claimed draft to a clean Pending state (Approving→Pending), clearing the minted-id that a
    // failed-late finalize may have staged. CancellationToken.None: releasing is a compensating write for a
    // side effect already committed (the claim), so it must complete even if the caller's token is cancelled.
    private Task ReleaseToPendingAsync(CanonDraft d)
    {
        d.Status = CanonDraftStatus.Pending;
        d.MintedMemoryId = null;
        return _drafts.UpdateAsync(d, CancellationToken.None);
    }

    // ─── Projection ───────────────────────────────────────────────────

    private static CanonDraftSummary ToSummary(CanonDraft d) => new(
        d.Id, d.Kind, d.Slug, d.Title, d.MemberIds.Count, d.ProposedAt,
        ReplacesExisting: d.SupersedesCanonId is not null,
        // Live fingerprint drift detection is P2 (it would re-run the sources on every list) — hardcode false.
        IsStale: false);

    private async Task<IReadOnlyList<Citation>> HydrateCitationsAsync(
        IReadOnlyList<string> memberIds, CancellationToken ct)
    {
        var citations = new List<Citation>(memberIds.Count);
        foreach (var id in memberIds)
        {
            MemoryEntry? m = null;
            try { m = await _store.GetAsync(id, ct); } catch { /* degrade to placeholder below */ }

            citations.Add(m is null
                ? new Citation(id, MemoryType.Observation, "(memory no longer available)", 0f, Href(id))
                : new Citation(id, m.Type, m.OneLiner ?? Truncate(m.Content, 100), m.Importance, Href(id)));
        }
        return citations;
    }

    private static string Href(string memoryId) => "#memory/" + memoryId;

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}

// ─── DTOs ─────────────────────────────────────────────────────────────

/// <summary>A cited member, rendered clickable in the review UI. <see cref="Href"/> is the SPA route
/// (<c>#memory/&lt;id&gt;</c>). A degraded citation (deleted member) carries a placeholder one-liner.</summary>
public sealed record Citation(string MemoryId, MemoryType Type, string OneLiner, float Importance, string Href);

public sealed record CanonDraftSummary(
    string Id, CanonKind Kind, string Slug, string Title, int MemberCount,
    DateTimeOffset ProposedAt, bool ReplacesExisting, bool IsStale);

public sealed record CanonDraftDetail(
    CanonDraftSummary Head, string ProposedContent, IReadOnlyList<Citation> Citations,
    string Fingerprint, DateTimeOffset? CooldownUntil);

public sealed record ApproveResult(bool Success, string DraftId, string? MintedMemoryId, string? Reason)
{
    public static ApproveResult NotFound(string id) => new(false, id, null, "not found");
    public static ApproveResult ClaimLost(string id) => new(false, id, null, "approve already in progress");
    public static ApproveResult NotPending(string id, CanonDraftStatus status) =>
        new(false, id, null, $"draft is {status.ToString().ToLowerInvariant()}, not pending — regenerate to re-propose");
}

public sealed record RejectResult(bool Success, string DraftId, DateTimeOffset? CooldownUntil, string? Reason)
{
    public static RejectResult NotFound(string id) => new(false, id, null, "not found");
}

public sealed record BulkApproveResult(int Approved, int Failed);
