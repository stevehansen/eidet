using Eidet.Core.Domain;

namespace Eidet.Core.Portal;

/// <summary>
/// Shared input passed to every <see cref="IPortalSection"/> during a single
/// render. The renderer pre-fetches all currently-valid memories and counts
/// once so each section can filter in memory without duplicate I/O.
/// </summary>
internal sealed class PortalContext
{
    public required string RepoId { get; init; }
    public required IReadOnlyList<MemoryEntry> AllValidMemories { get; init; }
    public required IReadOnlyDictionary<MemoryType, int> CountsByType { get; init; }
}
