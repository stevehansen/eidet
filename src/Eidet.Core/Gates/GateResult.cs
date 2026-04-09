namespace Eidet.Core.Gates;

public record GateResult(bool Passed, string? Reason = null)
{
    public static GateResult Pass() => new(true);
    public static GateResult Fail(string reason) => new(false, reason);
}
