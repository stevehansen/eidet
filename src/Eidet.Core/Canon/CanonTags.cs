using Eidet.Core.Domain;

namespace Eidet.Core.Canon;

/// <summary>
/// The <c>canon:*</c> tag namespace and the shared predicate that keeps approved Canon pages out of the
/// unsupervised maintenance stages. A curated page carries a human's judgment; consolidation must never
/// fold it into a machine insight, and dedup must never merge it away — so both filter it out of their
/// candidate sets via <see cref="IsCanonPage"/> (the valence-guard precedent: a filter, not a new stage).
/// </summary>
public static class CanonTags
{
    public const string Prefix = "canon:";

    public static string Term(string slug) => $"canon:term:{slug}";
    public static string Domain(string slug) => $"canon:domain:{slug}";

    /// <summary>True when the memory carries any <c>canon:*</c> tag — i.e. it is an approved Canon page.</summary>
    public static bool IsCanonPage(MemoryEntry entry) =>
        entry.Tags.Any(t => t.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
}
