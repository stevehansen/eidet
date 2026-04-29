using Eidet.Core.Domain;

namespace Eidet.Core.Layers;

/// <summary>
/// Immutable snapshot of the layer composition for a single recall request.
/// Resolved once at the transport boundary, then passed into the read pipeline so
/// <see cref="Memory.MemoryRecall"/> never has to know about layer mounting.
/// </summary>
/// <remarks>
/// <para><see cref="PrimaryRepoId"/> is the caller's normalised repo id. <see cref="RepoIds"/>
/// is the full set to search (always contains the primary, plus any extras pulled in by
/// mounted layers when <see cref="CrossRepo"/> is true).</para>
/// <para>Use <see cref="Local"/> for tests or single-repo callers that want no cross-repo
/// expansion. The de-boost factor for non-local hits is <see cref="NonLocalDeBoost"/>.</para>
/// </remarks>
public sealed record LayerScope(
    string PrimaryRepoId,
    IReadOnlyList<string> RepoIds,
    IReadOnlyList<MemoryLayer> MountedLayers,
    bool CrossRepo)
{
    /// <summary>De-boost factor applied to results from non-local layers (0.8× per spec).</summary>
    public const float NonLocalDeBoost = 0.8f;

    /// <summary>Single-repo scope with no mounted layers — for tests and non-cross-repo callers.</summary>
    public static LayerScope Local(string repoId) =>
        new(repoId, [repoId], [], CrossRepo: false);

    /// <summary>True if the entry belongs to the primary repo and is not from a mounted layer.</summary>
    public bool IsLocal(MemoryEntry entry) =>
        string.IsNullOrEmpty(entry.LayerId) &&
        string.Equals(entry.RepoId, PrimaryRepoId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Cheaper variant that operates on the search-result projection.</summary>
    public bool IsLocalRepo(string repoId) =>
        string.Equals(repoId, PrimaryRepoId, StringComparison.OrdinalIgnoreCase);
}
