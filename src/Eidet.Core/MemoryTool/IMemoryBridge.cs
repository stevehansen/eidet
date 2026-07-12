namespace Eidet.Core.MemoryTool;

/// <summary>
/// Optional one-way shadow from memory-tool files into the semantic store, reserving the seam
/// for typed/searchable memories without ever compromising the faithful blob path. Off by
/// default (<see cref="NullMemoryBridge"/>); the blob remains the source of truth either way.
/// </summary>
public interface IMemoryBridge
{
    /// <summary>Best-effort promotion of a written file into the semantic store. Failures never fail the write.</summary>
    Task PromoteAsync(string repoId, string path, string content, CancellationToken ct = default);

    /// <summary>Hybrid recall over the semantic store, surfaced via the read-only <c>/memories/.recall/&lt;query&gt;</c> path.</summary>
    Task<IReadOnlyList<(string Path, string Snippet)>> RecallAsync(string repoId, string q, int limit, CancellationToken ct = default);
}
