using Eidet.Core.Domain;

namespace Eidet.Core.Intake;

/// <summary>
/// Candidate memory emitted by an <see cref="IIntakeExtractor"/> through the
/// <see cref="IIntakeSink"/>. The orchestrator owns id construction, hashing, dedup,
/// and final <see cref="MemoryEntry"/> assembly — extractors never touch
/// <see cref="Storage.IEidetStore"/> directly.
/// </summary>
public sealed record IntakeMemory(
    string Source,
    MemoryType Type,
    string Content,
    IReadOnlyList<string> Tags,
    float Importance);
