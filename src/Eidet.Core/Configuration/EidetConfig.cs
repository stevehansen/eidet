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
    public UpdateConfig Update { get; set; } = new();
}

public class UpdateConfig
{
    /// <summary>
    /// Whether to look for new releases at all. Independent of <see cref="AutoUpdate"/>: with
    /// automation off this is what still surfaces "a new version exists" to a human.
    /// </summary>
    public bool Check { get; set; } = true;

    /// <summary>
    /// Install found updates without asking. Off by default and asked once during setup — a tool
    /// that silently replaces its own binary overnight should be a choice, not a discovery.
    /// </summary>
    public bool AutoUpdate { get; set; }

    /// <summary>Local wall-clock time for the nightly check, <c>HH:mm</c>.</summary>
    public string AtLocalTime { get; set; } = "04:00";

    /// <summary>
    /// How long a release must have existed before automation will install it. The fleet-level
    /// circuit breaker: releases are immutable, so a bad build can only be superseded, and this
    /// window is what buys time to publish the successor first.
    /// </summary>
    public int MinimumAgeHours { get; set; } = 24;

    /// <summary>
    /// <see cref="AtLocalTime"/> as a time, falling back to 04:00 rather than throwing — an
    /// unparseable value should cost the configured hour, not the whole scheduler.
    /// </summary>
    public TimeOnly ScheduledTime => LocalTimeSetting.Parse(AtLocalTime, new TimeOnly(4, 0));
}

/// <summary>
/// An <c>HH:mm</c> config value read as a time. Unparseable input yields the fallback rather than
/// throwing: a typo in config.json should cost the configured hour, not the whole scheduler.
/// </summary>
internal static class LocalTimeSetting
{
    public static TimeOnly Parse(string value, TimeOnly fallback) =>
        TimeOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
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

    /// <summary>
    /// Local wall-clock time the nightly pass is anchored to, <c>HH:mm</c>. The anchor is what
    /// keeps a long run from moving the series: without it the next run is scheduled from the
    /// previous one's completion, so a two-hour pass walks two hours later every day until it
    /// lands in the middle of the working day.
    /// </summary>
    public string AtLocalTime { get; set; } = "03:00";

    /// <summary>
    /// <see cref="AtLocalTime"/> as a time, falling back to 03:00 rather than throwing.
    /// </summary>
    public TimeOnly ScheduledTime => LocalTimeSetting.Parse(AtLocalTime, new TimeOnly(3, 0));
}

/// <summary>
/// One model server: where it is, how to talk to it, and which model to ask for. The primary
/// backend is the <see cref="EnrichmentConfig"/> itself (its flat keys predate fallbacks); each
/// entry of <see cref="EnrichmentConfig.Fallbacks"/> is another one of these.
/// </summary>
public class EnrichmentBackendConfig
{
    public EnrichmentProvider Provider { get; set; } = EnrichmentProvider.Ollama;
    public string Url { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "gemma4";

    /// <summary>Bearer token sent on every request when set. Needed for a private network cluster; local servers ignore it.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Whether the model should think out loud. Unset sends nothing and the server applies its
    /// default. <c>false</c> is the cost lever for a reasoning model on vLLM (DeepSeek, Qwen): it rides
    /// as <c>chat_template_kwargs.thinking</c>, the field the chat template honours. (The OpenAI
    /// <c>reasoning_effort</c> knob is a worse bet: <c>low</c>/<c>minimal</c> are accepted and ignored
    /// by those builds; <c>none</c> did work on deepseek-v4-flash-0731, 2026-09-04.) Ollama maps it to
    /// its native <c>think</c> field, off by default as before.
    /// </summary>
    public bool? Thinking { get; set; }
}

public class EnrichmentConfig : EnrichmentBackendConfig
{
    public bool Enabled { get; set; }
    public bool AutoOneLiner { get; set; } = true;
    public bool AutoForesight { get; set; } = true;
    public bool AutoConsolidation { get; set; } = true;
    public DriftReviewConfig DriftReview { get; set; } = new();
    public ReflectionConfig Reflection { get; set; } = new();

    /// <summary>
    /// Backends tried in order when the one before is offline or fails a call — a networked
    /// private model first, a local one behind it. Empty means the primary alone.
    /// </summary>
    public List<EnrichmentBackendConfig> Fallbacks { get; set; } = [];

    /// <summary>Primary first, then <see cref="Fallbacks"/> in order.</summary>
    [JsonIgnore]
    public IReadOnlyList<EnrichmentBackendConfig> Backends => [this, .. Fallbacks];
}

public class DriftReviewConfig
{
    public bool Enabled { get; set; } = true;             // still gated by EnrichmentConfig.Enabled at runtime
    public int NightlyBatch { get; set; } = 25;
    public int MinAgeDays { get; set; } = 7;

    /// <summary>
    /// How long a verdict stands before the entry is offered to the model again. This is what makes
    /// the stage converge: <c>Drift.ReviewedAt</c> doubles as the coverage cursor, so without a
    /// re-review interval the stage keeps handing the oldest verdicts back to the model forever —
    /// <see cref="NightlyBatch"/> calls per repo per night, on memories nothing has touched since.
    /// 0 restores that always-on behaviour.
    /// </summary>
    public int ReviewIntervalDays { get; set; } = 90;
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
