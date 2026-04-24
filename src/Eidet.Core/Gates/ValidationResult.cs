namespace Eidet.Core.Gates;

public readonly record struct ValidationResult(
    bool Passed,
    string? Reason = null,
    string? FailedGate = null)
{
    public static ValidationResult Pass() => new(true);
    public static ValidationResult Fail(string gate, string reason) => new(false, reason, gate);
}
