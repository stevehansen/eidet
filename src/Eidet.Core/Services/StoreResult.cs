using Eidet.Core.Memory;

namespace Eidet.Core.Services;

public class StoreResult
{
    public bool Success { get; init; }
    public string? Id { get; init; }
    public string? Reason { get; init; }
    public string? DuplicateId { get; init; }

    /// <summary>The store succeeded but the memory was held below trust pending feedback because it
    /// contradicts a high-trust incumbent. <see cref="Success"/> is still true — it is stored (append-only).</summary>
    public bool Quarantined { get; init; }
    public ConflictFinding? Conflict { get; init; }

    public static StoreResult Stored(string id) => new() { Success = true, Id = id };
    public static StoreResult Rejected(string reason) => new() { Success = false, Reason = reason };
    public static StoreResult Duplicate(string existingId) => new() { Success = false, DuplicateId = existingId, Reason = "Near-duplicate detected" };
    public static StoreResult QuarantinedPending(string id, ConflictFinding c) =>
        new() { Success = true, Id = id, Quarantined = true, Conflict = c, Reason = "Quarantined pending feedback" };
}
