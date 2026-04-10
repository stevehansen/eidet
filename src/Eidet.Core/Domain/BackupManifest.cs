namespace Eidet.Core.Domain;

public class BackupManifest
{
    public int Version { get; set; } = 1;
    public string EidetVersion { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string DatabaseName { get; set; } = "";
    public string StorageMode { get; set; } = "";
    public long DocumentCount { get; set; }
    public List<string> RepoIds { get; set; } = [];
    public string Checksum { get; set; } = "";
}
