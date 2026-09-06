using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

/// <summary>
/// Moves every live memory out of one repo namespace and into another.
///
/// Repo identity was the working directory verbatim, so a session run inside a git worktree — a PR
/// branch, a scratchpad checkout — banked its memories under the worktree's own path. Those memories
/// describe the main repository but are unreachable from it, and they outlive the directory they were
/// named after. <see cref="RepoPathResolver"/> stops new ones being stranded; this is the repair for
/// what was already banked, and it is deliberately a hand-aimed operation rather than a maintenance
/// stage: a stage would have to guess the target from a path that no longer exists.
///
/// Append-only, so nothing is relabelled. The entry is re-stored under the target repo — which mints a
/// fresh id, because the id commits to the repo along with the content — and the original is retired
/// with a reason naming where it went. Content the target already holds verbatim is retired without a
/// copy: sessions that stored to both the worktree and the primary checkout left the same memory in
/// both namespaces, and copying it would only mint a duplicate for a later sweep to fold. Either way
/// the source namespace ends up empty, which is the point — a half-emptied one still shadows recall.
/// Both namespaces are written inside one bulk scope, so each recall cache is invalidated exactly once.
/// </summary>
public sealed class RepoRehomeService
{
    /// <summary>Page size for reading a namespace out; the operation is bounded by repo size, not by this.</summary>
    private const int PageSize = 500;

    private readonly IEidetStore _store;
    private readonly MemoryService _memory;

    public RepoRehomeService(IEidetStore store, MemoryService memory)
    {
        _store = store;
        _memory = memory;
    }

    public async Task<RehomeResult> RehomeAsync(
        string fromRepo, string toRepo, bool dryRun = false, CancellationToken ct = default)
    {
        var from = RepoIdNormalizer.Normalize(fromRepo);
        var to = RepoIdNormalizer.Normalize(toRepo);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return new RehomeResult(from, to, 0, 0);

        var entries = await ReadNamespaceAsync(from, ct);

        // Content the target already holds. A memory stored from both the worktree and the primary
        // checkout would otherwise land twice and wait for a dedup sweep to fold what never needed
        // minting.
        var seen = (await ReadNamespaceAsync(to, ct))
            .Select(e => e.Content)
            .ToHashSet(StringComparer.Ordinal);

        var moved = 0;
        var folded = 0;

        if (dryRun)
        {
            foreach (var entry in entries)
            {
                if (seen.Add(entry.Content)) moved++;
                else folded++;
            }
            return new RehomeResult(from, to, moved, folded);
        }

        await _memory.RunBulkAsync(async ctx =>
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var isDuplicate = !seen.Add(entry.Content);

                if (!isDuplicate)
                {
                    // Mint the copy before retiring the original: interrupted midway this leaves a
                    // duplicate, which the exact-content fold already knows how to retire, rather than a
                    // memory that is live in neither namespace.
                    var copy = Clone(entry);
                    copy.RepoId = to;
                    copy.Id = MemoryIdGenerator.Generate(to, entry.Type, entry.Content, entry.CreatedAt);
                    await ctx.StoreNewAsync(copy, ct);
                    moved++;
                }
                else
                {
                    // The target already holds this content verbatim — copying it would mint a duplicate
                    // for a later sweep to retire. Leaving it live is worse still: the namespace this is
                    // emptying would survive, holding only the memories that were stored twice.
                    folded++;
                }

                // Either way the stranded original is retired, with a reason that says which happened.
                // No audit Observation is minted: one per memory would refill the namespace being
                // emptied, and the reason on the entry already carries the record.
                entry.Validity.ValidUntil = DateTime.UtcNow;
                entry.ForgetReason = isDuplicate
                    ? $"Already held by {to}; retired from worktree namespace {from}"
                    : $"Re-homed to {to} ({from} was a worktree of it)";
                await ctx.WriteAsync(entry, ct);
            }
            return 0;
        }, new BulkOptions { OperationName = "repo-rehome" }, ct);

        return new RehomeResult(from, to, moved, folded);
    }

    /// <summary>
    /// A faithful copy, taken through the serializer rather than field by field. A move that drops a
    /// field loses data with nothing reporting it, and <see cref="MemoryEntry"/> gains fields over
    /// time; the round trip also frees the copy from the original's identity, so this does not depend
    /// on whether a store hands back the instance it was given.
    /// </summary>
    private static MemoryEntry Clone(MemoryEntry entry) =>
        JsonSerializer.Deserialize<MemoryEntry>(JsonSerializer.Serialize(entry))!;

    private async Task<List<MemoryEntry>> ReadNamespaceAsync(string repoId, CancellationToken ct)
    {
        var all = new List<MemoryEntry>();
        while (true)
        {
            // Read the whole namespace before writing any of it: the queries behind Browse are
            // index-backed and lag the writes this method is about to make, so paging while moving
            // would skip documents as the result set shifts underneath the cursor.
            var page = await _store.BrowseAsync(repoId, all.Count, PageSize, null, ct);
            all.AddRange(page);
            if (page.Count < PageSize) return all;
        }
    }
}

/// <param name="Moved">Memories re-stored under <paramref name="To"/> and retired from <paramref name="From"/>.</param>
/// <param name="Folded">Memories the target already held verbatim, retired from <paramref name="From"/> without a copy.</param>
public sealed record RehomeResult(string From, string To, int Moved, int Folded);
