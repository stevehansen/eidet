using Eidet.Core.Domain;

namespace Eidet.Core.Gates;

internal interface IValidationRule
{
    string Name { get; }
    ValidationResult Check(string content, MemoryType type);
}
