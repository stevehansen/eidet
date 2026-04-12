using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Configuration;

namespace Eidet.Core.Services;

public record VersionHistoryEntry(
    string Version,
    DateTimeOffset InstalledAt,
    string? PreviousVersion,
    string Source // "dotnet-tool-install", "dotnet-tool-update", "manual"
);

public static class VersionHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string GetHistoryPath() =>
        Path.Combine(ConfigManager.GetConfigDir(), "version-history.json");

    public static List<VersionHistoryEntry> Load()
    {
        var path = GetHistoryPath();
        if (!File.Exists(path))
            return [];

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<VersionHistoryEntry>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Record(string version, string? previousVersion, string source)
    {
        var entries = Load();
        entries.Add(new VersionHistoryEntry(version, DateTimeOffset.UtcNow, previousVersion, source));

        var dir = Path.GetDirectoryName(GetHistoryPath())!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(GetHistoryPath(), JsonSerializer.Serialize(entries, JsonOptions));
    }

    public static VersionHistoryEntry? GetCurrent()
    {
        var entries = Load();
        return entries.Count > 0 ? entries[^1] : null;
    }
}
