namespace Eidet.Core.Services;

public class StoreResult
{
    public bool Success { get; init; }
    public string? Id { get; init; }
    public string? Reason { get; init; }
    public string? DuplicateId { get; init; }

    public static StoreResult Stored(string id) => new() { Success = true, Id = id };
    public static StoreResult Rejected(string reason) => new() { Success = false, Reason = reason };
    public static StoreResult Duplicate(string existingId) => new() { Success = false, DuplicateId = existingId, Reason = "Near-duplicate detected" };
}
