using Eidet.Core.Domain;

namespace Eidet.Core.Intake;

/// <summary>
/// Output port handed to extractors. The orchestrator's implementation handles
/// dedup-by-hash, store writes (gated by <see cref="IntakeContext.DryRun"/>), and
/// result accumulation. Extractors never see the document store directly.
/// </summary>
public interface IIntakeSink
{
    /// <summary>Emit a candidate memory. Hash-deduplicated and persisted by the orchestrator.</summary>
    ValueTask AddMemoryAsync(IntakeMemory candidate, CancellationToken ct);

    /// <summary>Record a detected dependency link (NuGet, npm, etc.).</summary>
    void AddLink(MemoryLink link);

    /// <summary>Record a package this repo produces (e.g. NuGet PackageId).</summary>
    void AddProducedPackage(string packageId);

    /// <summary>Record an item the extractor inspected but skipped, with a human reason.</summary>
    void RecordSkipped(string source, string reason);
}
