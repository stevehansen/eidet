using Eidet.Core.Domain;
using Eidet.Core.Intake;

namespace Eidet.Core.Tests.Intake;

/// <summary>Captures emitted memories/links/packages for extractor unit tests.</summary>
internal sealed class FakeIntakeSink : IIntakeSink
{
    public List<IntakeMemory> Memories { get; } = [];
    public List<MemoryLink> Links { get; } = [];
    public List<string> ProducedPackages { get; } = [];
    public List<(string Source, string Reason)> Skipped { get; } = [];

    public ValueTask AddMemoryAsync(IntakeMemory candidate, CancellationToken ct)
    {
        Memories.Add(candidate);
        return ValueTask.CompletedTask;
    }

    public void AddLink(MemoryLink link) => Links.Add(link);

    public void AddProducedPackage(string packageId) => ProducedPackages.Add(packageId);

    public void RecordSkipped(string source, string reason) => Skipped.Add((source, reason));
}
