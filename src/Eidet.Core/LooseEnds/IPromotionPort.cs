using Eidet.Core.Domain;

namespace Eidet.Core.LooseEnds;

/// <summary>
/// The ONLY edge from a Loose End into the gated memory write path. Promoting a confirmed Loose
/// End either mints a <c>MemoryEntry</c> through the full secret+signal gate, or links an external
/// issue without minting. Keeping this behind a port is the guardrail that stops a future caller
/// from routing park through the gated builder (which would start rejecting the terse notes the
/// feature exists to keep).
/// </summary>
public interface IPromotionPort
{
    Task<PromotionResult> PromoteAsync(LooseEnd e, PromoteOptions opts, CancellationToken ct = default);
}

public sealed record PromoteOptions(MemoryType Type, float Importance, string? ExternalRef);

public sealed record PromotionResult(bool Success, string? MemoryId, string? ExternalRef, string? Reason);
