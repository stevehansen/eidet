namespace Eidet.Core.Canon;

/// <summary>
/// The ONLY edge from a Canon draft into the gated memory write path (the <c>IPromotionPort</c> twin).
/// Approving a draft mints a <c>canon:*</c>-tagged <c>MemoryEntry</c> through the full
/// <see cref="Eidet.Core.Services.MemoryService"/> gate — secret + signal scan, dedup, hooks, versioning.
/// Keeping this behind a port is the guardrail that stops a future caller from writing synthesized prose
/// into <c>memories/*</c> by any other route: Approve is the sole write edge, this adapter enforces it.
/// </summary>
public interface ICanonMintPort
{
    Task<CanonMintResult> MintAsync(CanonDraft draft, string? editedContent, CancellationToken ct = default);
}

public sealed record CanonMintResult(bool Success, string? MemoryId, string? Reason);
