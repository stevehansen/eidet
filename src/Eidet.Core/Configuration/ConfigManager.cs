using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Eidet.Core.Configuration;

public static class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GetConfigDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Eidet");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".eidet");
    }

    public static string GetConfigPath() => Path.Combine(GetConfigDir(), "config.json");

    public static EidetConfig Load()
    {
        var path = GetConfigPath();
        EidetConfig config;
        if (!File.Exists(path))
            config = new EidetConfig();
        else
        {
            var json = File.ReadAllText(path);
            json = MigrateLegacyEnrichmentKeys(json);
            config = JsonSerializer.Deserialize<EidetConfig>(json, JsonOptions) ?? new EidetConfig();
        }

        // Environment variable overrides
        ApplyEnvironmentOverrides(config);
        return config;
    }

    // Pre-v0.2 configs stored enrichment under ollamaEnabled/ollamaUrl/ollamaModel.
    // Map those onto the new enabled/url/model keys when the new keys are absent,
    // so existing config.json files keep working without manual edits.
    private static string MigrateLegacyEnrichmentKeys(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return json; }

        if (root?["enrichment"] is not JsonObject enrichment) return json;

        var migrated = false;
        migrated |= RenameKey(enrichment, "ollamaEnabled", "enabled");
        migrated |= RenameKey(enrichment, "ollamaUrl", "url");
        migrated |= RenameKey(enrichment, "ollamaModel", "model");

        return migrated ? root!.ToJsonString() : json;
    }

    private static bool RenameKey(JsonObject obj, string legacy, string current)
    {
        if (obj.ContainsKey(current) || !obj.ContainsKey(legacy)) return false;
        var node = obj[legacy];
        obj.Remove(legacy);
        obj[current] = node;
        return true;
    }

    private static void ApplyEnvironmentOverrides(EidetConfig config)
    {
        // EIDET_API_URL — override service bind address and port (for containers/remote)
        var apiUrl = Environment.GetEnvironmentVariable("EIDET_API_URL");
        if (!string.IsNullOrEmpty(apiUrl) && Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri))
        {
            config.Service.BindAddress = uri.Host;
            config.Service.Port = uri.Port > 0 ? uri.Port : config.Service.Port;
        }

        // EIDET_RAVEN_URL — override RavenDB connection
        var ravenUrl = Environment.GetEnvironmentVariable("EIDET_RAVEN_URL");
        if (!string.IsNullOrEmpty(ravenUrl))
            config.Storage.RavenUrl = ravenUrl;

        // EIDET_ENRICHMENT_URL (EIDET_OLLAMA_URL = legacy alias) — override enrichment endpoint
        var enrichUrl = Environment.GetEnvironmentVariable("EIDET_ENRICHMENT_URL")
            ?? Environment.GetEnvironmentVariable("EIDET_OLLAMA_URL");
        if (!string.IsNullOrEmpty(enrichUrl))
            config.Enrichment.Url = enrichUrl;

        // EIDET_ENRICHMENT_MODEL (EIDET_OLLAMA_MODEL = legacy alias) — override enrichment model
        var enrichModel = Environment.GetEnvironmentVariable("EIDET_ENRICHMENT_MODEL")
            ?? Environment.GetEnvironmentVariable("EIDET_OLLAMA_MODEL");
        if (!string.IsNullOrEmpty(enrichModel))
            config.Enrichment.Model = enrichModel;

        // EIDET_STORAGE_MODE — override storage mode (embedded/external)
        var storageMode = Environment.GetEnvironmentVariable("EIDET_STORAGE_MODE");
        if (!string.IsNullOrEmpty(storageMode) && Enum.TryParse<StorageMode>(storageMode, true, out var mode))
            config.Storage.Mode = mode;

        // EIDET_DATA_DIR — override embedded RavenDB data directory
        var dataDir = Environment.GetEnvironmentVariable("EIDET_DATA_DIR");
        if (!string.IsNullOrEmpty(dataDir))
            config.Storage.DataDir = dataDir;

        // EIDET_AUTH_REQUIRE_NONLOCALHOST — override non-localhost auth guard
        var authGuard = Environment.GetEnvironmentVariable("EIDET_AUTH_REQUIRE_NONLOCALHOST");
        if (!string.IsNullOrEmpty(authGuard) && bool.TryParse(authGuard, out var requireAuth))
            config.Auth.RequireForNonLocalhost = requireAuth;
    }

    public static void Save(EidetConfig config)
    {
        var path = GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }
}
