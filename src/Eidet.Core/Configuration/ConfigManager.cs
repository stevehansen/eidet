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
    }

    public static void Save(EidetConfig config)
    {
        var path = GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }
}
