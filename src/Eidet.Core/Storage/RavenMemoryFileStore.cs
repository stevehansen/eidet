using Eidet.Core.MemoryTool;
using Raven.Client.Documents;

namespace Eidet.Core.Storage;

/// <summary>
/// RavenDB adapter for <see cref="IMemoryFileStore"/>. The <see cref="MemoryFile"/> CLR type
/// lands in its own <c>MemoryFiles</c> collection (ids <c>memoryfiles/{repo}/{path}</c>),
/// overwritten in place — structurally outside every memory-maintenance stage (which all query
/// <c>MemoryEntry</c>), so no decay/dedup/supersession can ever rewrite Claude's bytes.
/// Edit history/audit comes from RavenDB revisions, enabled bounded on this collection by
/// <see cref="DatabaseProvisioner.EnsureMemoryFileRevisions"/>.
/// All operations use id prefixes (<c>LoadStartingWith</c>), never indexes, so reads are
/// immediately consistent with writes — the tool loop depends on read-your-writes.
/// Note: RavenDB ids are case-insensitive, so paths differing only in case share one blob.
/// </summary>
public sealed class RavenMemoryFileStore : IMemoryFileStore
{
    private const int PageSize = 128;

    private readonly IDocumentStore _store;

    public RavenMemoryFileStore(IDocumentStore store) => _store = store;

    public async Task<bool> ExistsAsync(string repoId, string path, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        return await session.Advanced.ExistsAsync(MemoryFile.MakeId(repoId, path), ct);
    }

    public async Task<string?> ReadAsync(string repoId, string path, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var file = await session.LoadAsync<MemoryFile>(MemoryFile.MakeId(repoId, path), ct);
        return file?.Content;
    }

    public async Task WriteAsync(string repoId, string path, string content, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var id = MemoryFile.MakeId(repoId, path);
        var file = await session.LoadAsync<MemoryFile>(id, ct);
        if (file is null)
        {
            file = new MemoryFile
            {
                Id = id,
                RepoId = repoId,
                Path = path,
                CreatedAt = DateTime.UtcNow,
            };
            await session.StoreAsync(file, id, ct);
        }
        file.Content = content;
        file.UpdatedAt = DateTime.UtcNow;
        await session.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string repoId, string path, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        session.Delete(MemoryFile.MakeId(repoId, path));
        await session.SaveChangesAsync(ct);
    }

    public async Task MoveAsync(string repoId, string oldPath, string newPath, CancellationToken ct = default)
    {
        // Copy + delete in one session = one atomic SaveChanges.
        using var session = _store.OpenAsyncSession();
        var old = await session.LoadAsync<MemoryFile>(MemoryFile.MakeId(repoId, oldPath), ct);
        if (old is null) return;

        var newId = MemoryFile.MakeId(repoId, newPath);
        await session.StoreAsync(new MemoryFile
        {
            Id = newId,
            RepoId = repoId,
            Path = newPath,
            Content = old.Content,
            CreatedAt = old.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
        }, newId, ct);
        session.Delete(old);
        await session.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string repoId, string dir, CancellationToken ct = default)
    {
        var idPrefix = MemoryFile.MakeId(repoId, dir.TrimEnd('/') + "/");
        var result = new List<string>();
        using var session = _store.OpenAsyncSession();
        for (var start = 0; ; start += PageSize)
        {
            var page = await session.Advanced.LoadStartingWithAsync<MemoryFile>(
                idPrefix, start: start, pageSize: PageSize, token: ct);
            var count = 0;
            foreach (var file in page)
            {
                result.Add(file.Path);
                count++;
            }
            if (count < PageSize) break;
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
