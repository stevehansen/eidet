using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Raven.Client.Documents;

namespace Eidet.Core.Storage;

/// <summary>
/// RavenDB adapter for <see cref="IPoisonLog"/>. Recorded patterns land in a dedicated
/// <c>PoisonPatterns</c> collection (via the <see cref="PoisonPattern"/> CLR type), written OUTSIDE
/// <c>memories/*</c> so a poison write never perturbs the recall cache's per-scope generation
/// invariant or any maintenance sweep. The document id is deterministic
/// (<c>poisonpatterns/{repoId}/{fingerprint}</c>), so <see cref="MatchAsync"/> is a single keyed load
/// and a repeat attempt bumps the same document rather than minting a new one. No decay.
/// </summary>
public sealed class RavenPoisonLog : IPoisonLog
{
    private readonly IDocumentStore _store;

    public RavenPoisonLog(IDocumentStore store) => _store = store;

    public async Task<PoisonPattern?> MatchAsync(string repoId, string content, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        return await session.LoadAsync<PoisonPattern>(IdFor(repoId, content), ct);
    }

    public async Task RecordAsync(string repoId, ConflictFinding conflict, string content, CancellationToken ct = default)
    {
        var fingerprint = IPoisonLog.Fingerprint(content);
        var id = $"poisonpatterns/{repoId}/{fingerprint}";
        var now = DateTime.UtcNow;

        using var session = _store.OpenAsyncSession();
        var existing = await session.LoadAsync<PoisonPattern>(id, ct);
        if (existing is not null)
        {
            existing.Attempts++;
            existing.LastSeenAt = now;
            existing.ContradictedId = conflict.ContradictedId;
            existing.ContradictedTrust = conflict.ContradictedTrust;
        }
        else
        {
            await session.StoreAsync(new PoisonPattern
            {
                Id = id,
                RepoId = repoId,
                Fingerprint = fingerprint,
                ContradictedId = conflict.ContradictedId,
                Stance = conflict.Stance,
                ContradictedStance = conflict.ContradictedStance,
                ContradictedTrust = conflict.ContradictedTrust,
                SampleContent = content.Length > 280 ? content[..280] : content,
                Attempts = 1,
                FirstSeenAt = now,
                LastSeenAt = now,
            }, id, ct);
        }
        await session.SaveChangesAsync(ct);
    }

    private static string IdFor(string repoId, string content) =>
        $"poisonpatterns/{repoId}/{IPoisonLog.Fingerprint(content)}";
}
