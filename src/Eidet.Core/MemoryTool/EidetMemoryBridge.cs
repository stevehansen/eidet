using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.MemoryTool;

/// <summary>
/// Opt-in production bridge: promotes memory-tool files into the semantic store as
/// <c>memory-tool</c>-tagged observations (through the full write gate — the semantic
/// store's rules apply there, unlike the blob path) and answers <c>/memories/.recall</c>
/// queries via hybrid recall. One-way: nothing here ever rewrites a blob.
/// </summary>
public sealed class EidetMemoryBridge : IMemoryBridge
{
    private const string Source = "memory-tool";

    private readonly MemoryService _memory;

    public EidetMemoryBridge(MemoryService memory) => _memory = memory;

    public async Task PromoteAsync(string repoId, string path, string content, CancellationToken ct = default)
    {
        // Rejections (low-signal scratch files) and duplicates are expected — the semantic
        // shadow only keeps what passes the semantic store's own gates.
        await _memory.StoreAsync(new StoreOptions(repoId, content, MemoryType.Observation)
        {
            Tags = ["memory-tool", path],
            Source = Source,
        }, ct);
    }

    public async Task<IReadOnlyList<(string Path, string Snippet)>> RecallAsync(
        string repoId, string q, int limit, CancellationToken ct = default)
    {
        var results = await _memory.RecallAsync(repoId, new RecallOptions(q) { Limit = limit, CrossRepo = false }, ct);
        return results
            .Select(r => (PathFromTags(r.Tags) ?? r.Id, r.OneLiner ?? Truncate(r.Content)))
            .ToList();
    }

    private static string? PathFromTags(List<string> tags) =>
        tags.FirstOrDefault(t => t.StartsWith(MemoryPath.Root + "/", StringComparison.Ordinal));

    private static string Truncate(string content) =>
        content.Length <= 120 ? content : content[..117] + "...";
}
