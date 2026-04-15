namespace Eidet.Core.Domain;

public class MemoryLayer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public LayerType Type { get; set; }
    public bool ReadOnly { get; set; }
    public string? SourcePath { get; set; }
    public string? Version { get; set; }
    public DateTime MountedAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public int Priority { get; set; } // local=100, shared=50, base=10
    public List<string> ApplicableRepos { get; set; } = [];
    public List<string> ApplicablePackages { get; set; } = [];
}

public enum LayerType { Local, Shared, Base }
