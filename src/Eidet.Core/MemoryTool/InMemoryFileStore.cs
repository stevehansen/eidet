using System.Collections.Concurrent;

namespace Eidet.Core.MemoryTool;

/// <summary>In-process <see cref="IMemoryFileStore"/> — lets the translator be tested end-to-end with no RavenDB.</summary>
public sealed class InMemoryFileStore : IMemoryFileStore
{
    private readonly ConcurrentDictionary<(string RepoId, string Path), string> _files = new();

    public Task<bool> ExistsAsync(string repoId, string path, CancellationToken ct = default) =>
        Task.FromResult(_files.ContainsKey((repoId, path)));

    public Task<string?> ReadAsync(string repoId, string path, CancellationToken ct = default) =>
        Task.FromResult(_files.TryGetValue((repoId, path), out var content) ? content : null);

    public Task WriteAsync(string repoId, string path, string content, CancellationToken ct = default)
    {
        _files[(repoId, path)] = content;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string repoId, string path, CancellationToken ct = default)
    {
        _files.TryRemove((repoId, path), out _);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string repoId, string oldPath, string newPath, CancellationToken ct = default)
    {
        if (_files.TryRemove((repoId, oldPath), out var content))
            _files[(repoId, newPath)] = content;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string repoId, string dir, CancellationToken ct = default)
    {
        var prefix = dir.TrimEnd('/') + "/";
        IReadOnlyList<string> result = _files.Keys
            .Where(k => k.RepoId == repoId && k.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }
}
