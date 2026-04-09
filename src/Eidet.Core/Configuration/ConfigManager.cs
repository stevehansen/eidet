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
        if (!File.Exists(path))
            return new EidetConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<EidetConfig>(json, JsonOptions) ?? new EidetConfig();
    }

    public static void Save(EidetConfig config)
    {
        var path = GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }
}
