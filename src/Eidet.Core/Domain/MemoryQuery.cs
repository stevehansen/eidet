namespace Eidet.Core.Domain;

public class MemoryQuery
{
    public string Text { get; set; } = "";
    public MemoryType? Type { get; set; }
    public List<string> Tags { get; set; } = [];
    public int Limit { get; set; } = 10;
    public bool IncludeExpired { get; set; }
    public bool CrossRepo { get; set; }
}

public class MemorySearchResult
{
    public string Id { get; set; } = "";
    public string RepoId { get; set; } = "";
    public MemoryType Type { get; set; }
    public string Content { get; set; } = "";
    public string? Summary { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<string> Entities { get; set; } = [];
    public float Importance { get; set; }
    public string? OneLiner { get; set; }
    public DateTime CreatedAt { get; set; }
    public float Score { get; set; }
    public string? LayerSource { get; set; }
    public int AgeDays { get; set; }
    public string? StalenessWarning { get; set; }
    public bool IsSuperseded { get; set; }
}
