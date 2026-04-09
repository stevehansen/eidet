using Eidet.Core.Domain;

namespace Eidet.Core.Gates;

public static class WriteGate
{
    public static GateResult Validate(string content, MemoryType type = MemoryType.Observation)
    {
        var secretResult = SecretScanner.Scan(content);
        if (!secretResult.Passed) return secretResult;

        var signalResult = SignalGate.Check(content, type);
        if (!signalResult.Passed) return signalResult;

        return GateResult.Pass();
    }
}
