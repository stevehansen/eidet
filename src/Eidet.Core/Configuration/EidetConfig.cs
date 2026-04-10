using System.Text.Json.Serialization;

namespace Eidet.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<StorageMode>))]
public enum StorageMode { External, Embedded }

public class EidetConfig
{
    public ServiceConfig Service { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public MemoryConfig Memory { get; set; } = new();
    public MaintenanceConfig Maintenance { get; set; } = new();
    public EnrichmentConfig Enrichment { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public HooksConfig Hooks { get; set; } = new();
}

public class ServiceConfig
{
    public int Port { get; set; } = 19380;
    public string BindAddress { get; set; } = "127.0.0.1";
}

public class StorageConfig
{
    public StorageMode Mode { get; set; } = StorageMode.External;
    public string RavenUrl { get; set; } = "http://localhost:8080";
    public string DatabaseName { get; set; } = "Eidet";
    public string? DataDir { get; set; } // only for embedded mode
}

public class MemoryConfig
{
    public int L1Count { get; set; } = 20;
    public int L1MaxTokens { get; set; } = 500;
    public float DuplicateThreshold { get; set; } = 0.92f;
    public float VectorSimilarityMinimum { get; set; } = 0.70f;
    public int ObservationRetentionDays { get; set; } = 90;
    public bool AutoIntakeOnFirstSession { get; set; } = true;
    public bool CrossRepoRecallEnabled { get; set; } = true;
    public int StalenessWarningDays { get; set; } = 7;
    public bool RecallCacheEnabled { get; set; } = true;
}

public class MaintenanceConfig
{
    public int IntervalHours { get; set; } = 24;
    public int ConsolidationIntervalHours { get; set; } = 6;
}

public class EnrichmentConfig
{
    public bool OllamaEnabled { get; set; }
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "gemma4";
    public bool AutoOneLiner { get; set; } = true;
    public bool AutoForesight { get; set; } = true;
    public bool AutoConsolidation { get; set; } = true;
}

public class AuthConfig
{
    public bool Enabled { get; set; }
    public bool RequireForNonLocalhost { get; set; } = true;
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];
}

public class ApiKeyEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class HooksConfig
{
    public List<HookDefinition> PreStore { get; set; } = [];
    public List<HookDefinition> PostStore { get; set; } = [];
    public List<HookDefinition> PreRecall { get; set; } = [];
    public List<HookDefinition> PostRecall { get; set; } = [];
    public List<HookDefinition> PreForget { get; set; } = [];
    public List<HookDefinition> PostForget { get; set; } = [];
}

public class HookDefinition
{
    public string Command { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 10;
    public bool Enabled { get; set; } = true;
}
