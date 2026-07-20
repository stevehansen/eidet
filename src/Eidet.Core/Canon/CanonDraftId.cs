namespace Eidet.Core.Canon;

/// <summary>
/// Deterministic Canon draft document ids: <c>canondrafts/{repoId}/{kind}/{slug}</c> (lowercase kind).
/// Slug-keyed rather than content-hashed (unlike <c>MemoryEntry</c>) so a re-proposal for the same term
/// lands on the same document — the anchor the regeneration damper refreshes in place.
/// </summary>
public static class CanonDraftId
{
    public static string For(string repoId, CanonKind kind, string slug) =>
        $"canondrafts/{repoId}/{kind.ToString().ToLowerInvariant()}/{slug}";
}
