namespace Eidet.Core.Domain;

public class EidetPack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> ApplicablePackages { get; set; } = [];
    public List<MemoryEntry> Entries { get; set; } = [];
}
