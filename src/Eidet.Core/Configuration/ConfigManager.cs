using System.Text.Json;
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
            config = JsonSerializer.Deserialize<EidetConfig>(json, JsonOptions) ?? new EidetConfig();
        }

        // Environment variable overrides
        ApplyEnvironmentOverrides(config);
        return config;
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

        // EIDET_OLLAMA_URL — override Ollama connection
        var ollamaUrl = Environment.GetEnvironmentVariable("EIDET_OLLAMA_URL");
        if (!string.IsNullOrEmpty(ollamaUrl))
            config.Enrichment.OllamaUrl = ollamaUrl;

        // EIDET_OLLAMA_MODEL — override Ollama model
        var ollamaModel = Environment.GetEnvironmentVariable("EIDET_OLLAMA_MODEL");
        if (!string.IsNullOrEmpty(ollamaModel))
            config.Enrichment.OllamaModel = ollamaModel;

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
