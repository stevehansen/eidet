namespace Eidet.Core.MemoryTool;

/// <summary>Default bridge: no semantic shadow — writes stay blob-only, recall finds nothing.</summary>
public sealed class NullMemoryBridge : IMemoryBridge
{
    public static readonly NullMemoryBridge Instance = new();

    private NullMemoryBridge() { }

    public Task PromoteAsync(string repoId, string path, string content, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<(string Path, string Snippet)>> RecallAsync(string repoId, string q, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, string)>>([]);
}
