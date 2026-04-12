using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Eidet.Core;
using Eidet.Core.Services;
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

    private const string NuGetPackageId = "eidet";
    private const string NuGetIndexUrl = $"https://api.nuget.org/v3-flatcontainer/{NuGetPackageId}/index.json";

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var currentVersion = EidetVersion.Current;

        if (!settings.Json)
            AnsiConsole.MarkupLine($"Current version: [bold]{currentVersion}[/]");

        // Check NuGet for latest version
        var latestVersion = await GetLatestNuGetVersionAsync(cancellation);

        if (latestVersion == null)
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
                AnsiConsole.MarkupLine("[dim]Check manually: dotnet tool update -g eidet[/]");
            }
            return 1;
        }

        var isUpToDate = string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase);

        if (settings.Json && settings.CheckOnly)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                current = currentVersion,
                latest = latestVersion,
                upToDate = isUpToDate,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        if (isUpToDate && !settings.Force)
        {
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    current = currentVersion,
                    latest = latestVersion,
                    upToDate = true,
                    updated = false,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]Already up to date[/] (v{currentVersion})");
            }
            return 0;
        }

        if (!settings.Json)
            AnsiConsole.MarkupLine($"Latest version:  [green]{latestVersion}[/]");

        if (settings.CheckOnly)
        {
            if (!settings.Json)
            {
                AnsiConsole.MarkupLine($"[yellow]Update available![/] Run [dim]eidet update[/] to install.");
            }
            return 0;
        }

        // Perform the update: stop service → dotnet tool update → restart service
        if (!settings.Json)
            AnsiConsole.MarkupLine("[bold]Updating...[/]");

        // Step 1: Stop the service
        var serviceWasRunning = await StopServiceAsync(settings, cancellation);

        // Step 2: Run dotnet tool update
        var updateResult = await RunDotnetToolUpdateAsync(settings, cancellation);

        if (!updateResult.Success)
        {
            // Try to restart service even on failure
            if (serviceWasRunning)
                await StartServiceAsync(settings, cancellation);

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    current = currentVersion,
                    latest = latestVersion,
                    updated = false,
                    error = updateResult.Error,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Update failed:[/] {Markup.Escape(updateResult.Error ?? "Unknown error")}");
            }
            return 1;
        }

        // Step 3: Record in version history
        VersionHistory.Record(latestVersion, currentVersion, "dotnet-tool-update");

        // Step 4: Restart service
        if (serviceWasRunning)
            await StartServiceAsync(settings, cancellation);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                current = currentVersion,
                latest = latestVersion,
                updated = true,
                upToDate = true,
                serviceRestarted = serviceWasRunning,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]Updated to v{latestVersion}[/]");
            if (serviceWasRunning)
                AnsiConsole.MarkupLine("  Service restarted.");
        }

        return 0;
    }

    internal static async Task<string?> GetLatestNuGetVersionAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Eidet-Updater");

            var json = await http.GetStringAsync(NuGetIndexUrl, ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("versions", out var versions))
                return null;

            // NuGet returns versions in ascending order — last is latest stable
            string? latest = null;
            foreach (var v in versions.EnumerateArray())
            {
                var ver = v.GetString();
                if (ver != null && !ver.Contains('-')) // skip pre-release
                    latest = ver;
            }
            return latest;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> StopServiceAsync(Settings settings, CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Check if scheduled task is running
                var (exitCode, output) = await RunProcessAsync("schtasks.exe", "/query /tn \"Eidet\" /fo CSV /nh", ct);
                if (exitCode != 0)
                    return false; // Task doesn't exist

                if (output.Contains("Running", StringComparison.OrdinalIgnoreCase))
                {
                    if (!settings.Json)
                        AnsiConsole.MarkupLine("  Stopping service...");
                    await RunProcessAsync("schtasks.exe", "/end /tn \"Eidet\"", ct);
                    // Give the process time to release file locks
                    await Task.Delay(2000, ct);
                    return true;
                }
                return false;
            }
            else if (OperatingSystem.IsMacOS())
            {
                var (exitCode, _) = await RunProcessAsync("launchctl", "stop dev.eidet.service", ct);
                if (exitCode == 0)
                {
                    await Task.Delay(2000, ct);
                    return true;
                }
                return false;
            }
            else
            {
                var (exitCode, output) = await RunProcessAsync("systemctl", "--user is-active eidet.service", ct);
                if (exitCode == 0 && output.Trim() == "active")
                {
                    if (!settings.Json)
                        AnsiConsole.MarkupLine("  Stopping service...");
                    await RunProcessAsync("systemctl", "--user stop eidet.service", ct);
                    await Task.Delay(2000, ct);
                    return true;
                }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static async Task StartServiceAsync(Settings settings, CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!settings.Json)
                    AnsiConsole.MarkupLine("  Starting service...");
                await RunProcessAsync("schtasks.exe", "/run /tn \"Eidet\"", ct);
            }
            else if (OperatingSystem.IsMacOS())
            {
                await RunProcessAsync("launchctl", "start dev.eidet.service", ct);
            }
            else
            {
                await RunProcessAsync("systemctl", "--user start eidet.service", ct);
            }
        }
        catch { }
    }

    private static async Task<(bool Success, string? Error)> RunDotnetToolUpdateAsync(Settings settings, CancellationToken ct)
    {
        try
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine("  Running dotnet tool update...");

            var (exitCode, output) = await RunProcessAsync("dotnet", "tool update -g eidet", ct);

            if (exitCode == 0)
                return (true, null);

            return (false, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "Failed to start process");

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            var error = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, string.IsNullOrEmpty(output) ? error : output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
