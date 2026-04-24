using Eidet.Core.Domain;

namespace Eidet.Core.Gates;

public static class WriteValidator
{
    private static readonly IValidationRule[] Rules =
    [
        new SecretScanRule(),
        new SignalRule(),
    ];

    public static ValidationResult Validate(string content, MemoryType type = MemoryType.Observation)
    {
        foreach (var rule in Rules)
        {
            var result = rule.Check(content, type);
            if (!result.Passed) return result;
        }
        return ValidationResult.Pass();
    }
}
