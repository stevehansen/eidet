namespace Eidet.Core.Domain;

public class MemoryLink
{
    public string TargetRepoId { get; set; } = "";
    public string? TargetMemoryId { get; set; }
    public string Relation { get; set; } = ""; // "depends-on", "uses-library", "supports", "conflicts", "refines"
}
