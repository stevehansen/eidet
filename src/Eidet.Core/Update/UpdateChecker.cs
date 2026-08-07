using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Configuration;

namespace Eidet.Core.Update;

/// <summary>
/// Answers "is there a newer Eidet, and when was it published?" and remembers the answer.
///
/// The split between asking and remembering is the point: exactly one caller — the nightly
/// scheduled task — ever reaches the network, and every place that wants to *mention* an update
/// reads <see cref="ReadCache"/> instead. Without that split, every CLI invocation and every MCP
/// session start would pay a NuGet round-trip just to print one line it usually won't print.
/// </summary>
public sealed class UpdateChecker
{
    private const string PackageId = "eidet";
    private const string VersionIndexUrl = $"https://api.nuget.org/v3-flatcontainer/{PackageId}/index.json";
    private const string RegistrationLeafUrlFormat =
        $"https://api.nuget.org/v3/registration5-gz-semver2/{PackageId}/{{0}}.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Func<string, CancellationToken, Task<string?>> _fetch;
    private readonly string _cachePath;

    public UpdateChecker(
        Func<string, CancellationToken, Task<string?>>? fetch = null,
        string? cachePath = null)
    {
        _fetch = fetch ?? FetchOverHttpAsync;
        _cachePath = cachePath ?? DefaultCachePath();
    }

    public static string DefaultCachePath() =>
        Path.Combine(ConfigManager.GetConfigDir(), "update-check.json");

    /// <summary>
    /// Looks up the newest stable release and its publish date, then writes the result to the
    /// cache. Never throws: an unreachable NuGet returns null and leaves the previous cache in
    /// place, so a night without connectivity degrades to a stale notice rather than an error.
    /// </summary>
    public async Task<UpdateStatus?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        var indexJson = await _fetch(VersionIndexUrl, ct);
        if (indexJson is null) return null;

        var latest = SelectLatestStable(indexJson);
        if (latest is null) return null;

        // The flat-container index carries no dates, so the age gate needs one more hop. A single
        // registration leaf per candidate version avoids paging the whole registration index.
        DateTimeOffset? published = null;
        var leafJson = await _fetch(string.Format(RegistrationLeafUrlFormat, latest), ct);
        if (leafJson is not null)
            published = ReadPublishedDate(leafJson);

        var status = new UpdateStatus
        {
            Current = currentVersion,
            Latest = latest,
            LatestPublishedAt = published,
            CheckedAt = DateTimeOffset.UtcNow,
        };

        WriteCache(status);
        return status;
    }

    /// <summary>
    /// Reads the last cached check. Never touches the network and never throws — a missing or
    /// corrupt cache simply means "nothing to say".
    /// </summary>
    public static UpdateStatus? ReadCache(string? path = null)
    {
        try
        {
            var file = path ?? DefaultCachePath();
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<UpdateStatus>(File.ReadAllText(file), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void WriteCache(UpdateStatus status)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(status, JsonOptions));
        }
        catch
        {
            // A cache we can't write costs a notice, not a run.
        }
    }

    /// <summary>
    /// Picks the highest stable version from a flat-container index. NuGet lists ascending, but
    /// this sorts by SemVer rather than trusting order — an unparseable entry is skipped instead
    /// of becoming the answer.
    /// </summary>
    internal static string? SelectLatestStable(string indexJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(indexJson);
            if (!doc.RootElement.TryGetProperty("versions", out var versions))
                return null;

            string? best = null;
            SemanticVersion bestParsed = default;

            foreach (var element in versions.EnumerateArray())
            {
                var raw = element.GetString();
                if (!SemanticVersion.TryParse(raw, out var parsed)) continue;
                if (parsed.IsPreRelease) continue;
                if (best is null || parsed.CompareTo(bestParsed) > 0)
                {
                    best = raw;
                    bestParsed = parsed;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pulls <c>published</c> out of a NuGet registration leaf.</summary>
    internal static DateTimeOffset? ReadPublishedDate(string leafJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(leafJson);
            if (doc.RootElement.TryGetProperty("published", out var published)
                && published.TryGetDateTimeOffset(out var value))
                return value;

            if (doc.RootElement.TryGetProperty("catalogEntry", out var entry)
                && entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("published", out var nested)
                && nested.TryGetDateTimeOffset(out var nestedValue))
                return nestedValue;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> FetchOverHttpAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Eidet-Updater");
            return await http.GetStringAsync(url, ct);
        }
        catch
        {
            return null;
        }
    }
}
