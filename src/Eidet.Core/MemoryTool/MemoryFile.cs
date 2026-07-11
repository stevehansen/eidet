namespace Eidet.Core.MemoryTool;

/// <summary>
/// One memory-tool file as stored: a faithful blob in its own <c>MemoryFiles</c> collection
/// (id <c>memoryfiles/{repo}/{relative-path}</c>), mutated in place so line edits round-trip
/// byte-exact — deliberately outside the append-only <c>memories/*</c> lifecycle. RavenDB
/// revisions (when enabled) provide the audit trail.
/// </summary>
public sealed class MemoryFile
{
    public string Id { get; set; } = "";
    public string RepoId { get; set; } = "";

    /// <summary>Canonical memory path, e.g. <c>/memories/plans/auth.md</c>.</summary>
    public string Path { get; set; } = "";

    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static string MakeId(string repoId, string path) =>
        $"memoryfiles/{repoId}/{RelativeOf(path)}";

    private static string RelativeOf(string path) =>
        path == MemoryPath.Root ? "" : path[(MemoryPath.Root.Length + 1)..];
}
