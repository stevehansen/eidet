using System.Text.Json.Serialization;

namespace Eidet.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<StorageMode>))]
public enum StorageMode { External, Embedded }

[JsonConverter(typeof(JsonStringEnumConverter<EnrichmentProvider>))]
public enum EnrichmentProvider { Ollama, OpenAiCompatible }

[JsonConverter(typeof(JsonStringEnumConverter<DriftAutonomy>))]
public enum DriftAutonomy { FlagOnly, Decay, Expire }

public class EidetConfig
{
    public ServiceConfig Service { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public MemoryConfig Memory { get; set; } = new();
    public MaintenanceConfig Maintenance { get; set; } = new();
    public EnrichmentConfig Enrichment { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public HooksConfig Hooks { get; set; } = new();
    public BackupConfig Backup { get; set; } = new();
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

    // Retention lifecycle policy (#39) lives with the rest of the memory config.
    public BudgetConfig Budget { get; set; } = new();
    public DeprecateConfig Deprecate { get; set; } = new();
}

/// <summary>
/// Per-repo, per-type memory budget (#39). OFF by default ⇒ unbounded, no eviction. When enabled with a
/// positive cap, maintenance deterministically evicts the lowest-retention memories of each type down to
/// the cap via forget-with-reason (reversible soft-delete). Quarantined memories are never evicted.
/// </summary>
public sealed class BudgetConfig
{
    public bool Enabled { get; set; }                       // default false ⇒ no eviction
    public int MaxPerType { get; set; }                     // 0 = unbounded; caps EACH type, per repo
    public double EchoReinforcement { get; set; } = 0.5;    // β: how much echo usage shields from eviction
}

/// <summary>
/// Retirement of terminally-stale procedures (#39): forgets a Procedure only when it is FadeMem-floored
/// AND net-negative AND idle beyond <see cref="MinIdleDays"/> — the terminal subset RoiDecay can never
/// reach (RoiDecay only reversibly demotes Importance and never forgets). Conservative gate makes ON safe.
/// </summary>
public sealed class DeprecateConfig
{
    public bool Enabled { get; set; } = true;
    public int MinIdleDays { get; set; } = 180;             // Procedure half-life-scaled
}

public class MaintenanceConfig
{
    public int IntervalHours { get; set; } = 24;
    public int ConsolidationIntervalHours { get; set; } = 6;
}

public class EnrichmentConfig
{
    public bool Enabled { get; set; }
    public EnrichmentProvider Provider { get; set; } = EnrichmentProvider.Ollama;
    public string Url { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "gemma4";
    public bool AutoOneLiner { get; set; } = true;
    public bool AutoForesight { get; set; } = true;
    public bool AutoConsolidation { get; set; } = true;
    public DriftReviewConfig DriftReview { get; set; } = new();
    public ReflectionConfig Reflection { get; set; } = new();
}

public class DriftReviewConfig
{
    public bool Enabled { get; set; } = true;             // still gated by EnrichmentConfig.Enabled at runtime
    public int NightlyBatch { get; set; } = 25;
    public int MinAgeDays { get; set; } = 7;
    public float MinModelConfidence { get; set; } = 0.7f; // below: verdict recorded, no action
    public DriftAutonomy Autonomy { get; set; } = DriftAutonomy.Decay;
}

/// <summary>
/// The ACE-style Reflector: mints net-new memory candidates from positive feedback residue
/// (net-echoed memories, Done loose ends, Contradicted drift verdicts) via one maintenance-time
/// LLM call. Ships DORMANT (<see cref="Enabled"/> = false) — turn on deliberately per deployment.
/// Still gated by <see cref="EnrichmentConfig.Enabled"/> at runtime like <see cref="DriftReviewConfig"/>.
/// </summary>
public class ReflectionConfig
{
    public bool Enabled { get; set; }                     // dormant by default — opt in per deployment
    public int NightlyBatch { get; set; } = 10;           // total cap on residue items fed to the model per run
    public int MinEchoes { get; set; } = 3;               // net echoes (EchoCount − FizzleCount) to count as residue
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

public class BackupConfig
{
    public string BackupDir { get; set; } = "";
    public int RetainCount { get; set; } = 10;
    public int AutoBackupIntervalHours { get; set; } = 0; // 0 = disabled
}

public class HooksConfig
{
    public List<HookDefinition> PreStore { get; set; } = [];
    public List<HookDefinition> PostStore { get; set; } = [];
    public List<HookDefinition> PreRecall { get; set; } = [];
    public List<HookDefinition> PostRecall { get; set; } = [];
    public List<HookDefinition> PreForget { get; set; } = [];
    public List<HookDefinition> PostForget { get; set; } = [];

    /// <summary>True when at least one hook is configured and enabled across all six events.</summary>
    public bool AnyEnabled() =>
        PreStore.Concat(PostStore).Concat(PreRecall).Concat(PostRecall).Concat(PreForget).Concat(PostForget)
            .Any(h => h.Enabled);
}

public class HookDefinition
{
    public string Command { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 10;
    public bool Enabled { get; set; } = true;
}
