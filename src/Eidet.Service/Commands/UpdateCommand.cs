using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Eidet.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class UpdateCommand : AsyncCommand<UpdateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--check")]
        public bool CheckOnly { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }

        [CommandOption("--force")]
        public bool Force { get; set; }
    }

    private const string GitHubOwner = "stevehansen";
    private const string GitHubRepo = "eidet";
    private const string ReleasesApiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var currentVersion = EidetVersion.Current;

        AnsiConsole.MarkupLine($"Current version: [bold]{currentVersion}[/]");

        // Check for latest version
        var latest = await GetLatestReleaseAsync(cancellation);

        if (latest == null)
        {
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    current = currentVersion,
                    error = "Could not check for updates",
                }));
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Could not check for updates.[/]");
                AnsiConsole.MarkupLine("[dim]Check manually at https://github.com/stevehansen/eidet/releases[/]");
            }
            return 1;
        }

        var latestVersion = latest.Value.Version.TrimStart('v');
        var isUpToDate = string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                current = currentVersion,
                latest = latestVersion,
                upToDate = isUpToDate,
                downloadUrl = latest.Value.AssetUrl,
                releaseUrl = latest.Value.HtmlUrl,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        if (isUpToDate && !settings.Force)
        {
            AnsiConsole.MarkupLine($"[green]Already up to date[/] (v{currentVersion})");
            return 0;
        }

        AnsiConsole.MarkupLine($"Latest version:  [green]{latestVersion}[/]");

        if (settings.CheckOnly)
        {
            if (!isUpToDate)
            {
                AnsiConsole.MarkupLine($"[yellow]Update available![/] Run [dim]eidet update[/] to install.");
                AnsiConsole.MarkupLine($"  Release: {latest.Value.HtmlUrl}");
            }
            return 0;
        }

        // Download and install
        if (latest.Value.AssetUrl == null)
        {
            AnsiConsole.MarkupLine("[yellow]No downloadable asset found for this platform.[/]");
            AnsiConsole.MarkupLine($"  Download manually from: {latest.Value.HtmlUrl}");
            return 1;
        }

        var installDir = InstallCommand.GetInstallDir();
        var binaryName = OperatingSystem.IsWindows() ? "eidet.exe" : "eidet";
        var targetPath = Path.Combine(installDir, binaryName);
        var tempPath = targetPath + ".new";

        AnsiConsole.MarkupLine($"Downloading v{latestVersion}...");
        AnsiConsole.MarkupLine($"  Source: {Markup.Escape(latest.Value.AssetUrl!)}");

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Eidet-Updater");

            await using var downloadStream = await http.GetStreamAsync(latest.Value.AssetUrl, cancellation);
            await using var fileStream = File.Create(tempPath);
            await downloadStream.CopyToAsync(fileStream, cancellation);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Download failed:[/] {Markup.Escape(ex.Message)}");
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return 1;
        }

        // Replace binary
        try
        {
            var backupPath = targetPath + ".bak";
            if (File.Exists(targetPath))
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(targetPath, backupPath);
            }

            File.Move(tempPath, targetPath);

            // Set executable permission on Unix
            if (!OperatingSystem.IsWindows())
            {
                Process.Start("chmod", $"+x \"{targetPath}\"")?.WaitForExit();
            }

            AnsiConsole.MarkupLine($"[green]Updated to v{latestVersion}[/]");
            AnsiConsole.MarkupLine($"  Installed to: {targetPath}");

            // Clean up backup
            if (File.Exists(backupPath))
            {
                try { File.Delete(backupPath); }
                catch { /* May be locked on Windows — cleanup on next run */ }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Installation failed:[/] {Markup.Escape(ex.Message)}");
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return 1;
        }

        return 0;
    }

    internal static async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Eidet-Updater");

            var json = await http.GetStringAsync(ReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var version = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;

            // Find the right asset for this platform
            string? assetUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                var suffix = GetPlatformAssetSuffix();
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Contains(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        assetUrl = asset.TryGetProperty("browser_download_url", out var dl)
                            ? dl.GetString() : null;
                        break;
                    }
                }
            }

            return new ReleaseInfo
            {
                Version = version,
                HtmlUrl = htmlUrl,
                AssetUrl = assetUrl,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string GetPlatformAssetSuffix()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        return $"linux-{arch}";
    }

    internal struct ReleaseInfo
    {
        public string Version;
        public string? HtmlUrl;
        public string? AssetUrl;
    }
}
