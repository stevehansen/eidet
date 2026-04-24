using Eidet.Core.Domain;

namespace Eidet.Core.Gates;

internal sealed class SignalRule : IValidationRule
{
    public const string GateName = "signal";
    public string Name => GateName;

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

    public ValidationResult Check(string content, MemoryType type)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ValidationResult.Fail(GateName, "Content is empty.");

        var trimmed = content.Trim();

        if (trimmed.Length < 20)
            return ValidationResult.Fail(GateName,
                $"Content too short ({trimmed.Length} chars). Memories should be specific and self-contained (20+ chars).");

        var lower = trimmed.ToLowerInvariant();
        foreach (var pattern in LowSignalPatterns)
        {
            if (lower == pattern || lower == pattern + ".")
                return ValidationResult.Fail(GateName,
                    $"Content matches low-signal pattern (\"{pattern}\"). Store specific, actionable knowledge instead.");
        }

        if (type == MemoryType.Observation &&
            (lower.StartsWith("i will ") || lower.StartsWith("let me ") || lower.StartsWith("i'm going to ")))
            return ValidationResult.Fail(GateName,
                "Content appears to be agent self-talk, not a project fact. Store what you learned, not what you're doing.");

        return ValidationResult.Pass();
    }
}
