namespace Eidet.Core.MemoryTool;

/// <summary>
/// Faithful blob store for memory-tool files: byte-exact, path-keyed, overwrite-in-place —
/// deliberately none of the semantic store's machinery (no content-addressed ids, dedup,
/// decay, or supersession), because <c>str_replace</c>/<c>insert</c> are verbatim line edits
/// and Claude must re-read exactly what it wrote. Paths are canonical <see cref="MemoryPath"/>
/// strings (<c>/memories/...</c>); repo ids are pre-normalized by the translator.
/// </summary>
public interface IMemoryFileStore
{
    Task<bool> ExistsAsync(string repoId, string path, CancellationToken ct = default);

    /// <summary>File content, or null when the path has no file.</summary>
    Task<string?> ReadAsync(string repoId, string path, CancellationToken ct = default);

    /// <summary>Create or overwrite in place.</summary>
    Task WriteAsync(string repoId, string path, string content, CancellationToken ct = default);

    Task DeleteAsync(string repoId, string path, CancellationToken ct = default);

    Task MoveAsync(string repoId, string oldPath, string newPath, CancellationToken ct = default);

    /// <summary>All file paths at or below <paramref name="dir"/> (recursive), canonical form.</summary>
    Task<IReadOnlyList<string>> ListAsync(string repoId, string dir, CancellationToken ct = default);
}
