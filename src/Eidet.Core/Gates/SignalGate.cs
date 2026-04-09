using Eidet.Core.Domain;

namespace Eidet.Core.Gates;

public static class SignalGate
{
    private static readonly string[] LowSignalPatterns =
    [
        "file exists",
        "file does not exist",
        "ran tests",
        "tests passed",
        "tests failed",
        "build succeeded",
        "build failed",
        "no changes",
        "no errors",
        "everything works",
        "it works",
        "fixed it",
        "done",
        "ok",
        "updated",
        "changed",
        "modified",
    ];

    public static GateResult Check(string content, MemoryType type = MemoryType.Observation)
    {
        if (string.IsNullOrWhiteSpace(content))
            return GateResult.Fail("Content is empty.");

        var trimmed = content.Trim();

        if (trimmed.Length < 20)
            return GateResult.Fail($"Content too short ({trimmed.Length} chars). Memories should be specific and self-contained (20+ chars).");

        var lower = trimmed.ToLowerInvariant();
        foreach (var pattern in LowSignalPatterns)
        {
            if (lower == pattern || lower == pattern + ".")
                return GateResult.Fail($"Content matches low-signal pattern (\"{pattern}\"). Store specific, actionable knowledge instead.");
        }

        if (type == MemoryType.Observation &&
            (lower.StartsWith("i will ") || lower.StartsWith("let me ") || lower.StartsWith("i'm going to ")))
            return GateResult.Fail("Content appears to be agent self-talk, not a project fact. Store what you learned, not what you're doing.");

        return GateResult.Pass();
    }
}
