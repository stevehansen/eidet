using System.Security.Cryptography;
using System.Text;
using Eidet.Core.Domain;

namespace Eidet.Core.Memory;

/// <summary>
/// Append-only log of contradiction attempts, keyed by a content fingerprint. Lets
/// <c>MemoryService.StoreAsync</c> fast-path a repeat attempt to Rejected before any similarity
/// query, and preserves the evidence across restarts. Backed by a dedicated <c>poisonpatterns/*</c>
/// collection in production; <see cref="NullPoisonLog"/> is the zero-overhead default.
/// </summary>
public interface IPoisonLog
{
    /// <summary>The recorded pattern for this content in this repo, or null if none.</summary>
    Task<PoisonPattern?> MatchAsync(string repoId, string content, CancellationToken ct = default);

    /// <summary>Record (or bump the attempt count of) a contradiction for this content.</summary>
    Task RecordAsync(string repoId, ConflictFinding conflict, string content, CancellationToken ct = default);

    /// <summary>
    /// Deterministic fingerprint of a memory's content — the first 16 hex chars of SHA256 over the
    /// case-folded, whitespace-collapsed content. Shared by every implementation so an id computed on
    /// record matches the id looked up on match.
    /// </summary>
    static string Fingerprint(string content)
    {
        var normalized = string.Join(' ', content.ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

/// <summary>No-op poison log — the default when the feature is not wired. Never records, never matches.</summary>
public sealed class NullPoisonLog : IPoisonLog
{
    public static readonly NullPoisonLog Instance = new();
    private NullPoisonLog() { }

    public Task<PoisonPattern?> MatchAsync(string repoId, string content, CancellationToken ct = default) =>
        Task.FromResult<PoisonPattern?>(null);

    public Task RecordAsync(string repoId, ConflictFinding conflict, string content, CancellationToken ct = default) =>
        Task.CompletedTask;
}
