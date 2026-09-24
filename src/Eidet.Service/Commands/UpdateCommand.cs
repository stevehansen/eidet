using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Eidet.Core;
using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Update;
using Eidet.Service.Update;
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

        /// <summary>
        /// Install this exact version instead of resolving the newest one. The unattended path
        /// uses it so the version the scheduler vetted — age gate included — is the version that
        /// actually lands, rather than whatever NuGet's latest happens to be moments later.
        /// </summary>
        [CommandOption("--to <VERSION>")]
        public string? To { get; set; }

        /// <summary>
        /// Reinstall the version recorded before the current one. Reliable precisely because
        /// releases are immutable: the previous version is guaranteed to still be there, and to be
        /// the same bytes that were working an hour ago.
        /// </summary>
        [CommandOption("--rollback")]
        public bool Rollback { get; set; }

        // Hidden flag invoked on the freshly-installed binary by UpdateInPlaceAsync to record version history *after* dotnet tool update has actually
        // replaced the on-disk binary. The running process reports its own
        // EidetVersion.Current as the installed version — so this only records truth.
        [CommandOption("--record-installed-from <PREVIOUS>")]
        public string? RecordInstalledFrom { get; set; }

        // Optional sanity check paired with --record-installed-from: if the freshly
        // launched binary's EidetVersion.Current does not match this value, refuse to
        // record (catches the "dotnet tool update exited 0 but installed nothing" case).
        [CommandOption("--expected-version <VERSION>")]
        public string? ExpectedVersion { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var currentVersion = EidetVersion.Current;

        // Post-install callback: record version history from the freshly-installed binary.
        // This is invoked by UpdateInPlaceAsync after a successful dotnet tool update. It deliberately bypasses the NuGet check.
        if (settings.RecordInstalledFrom is not null)
            return RecordInstalledVersion(currentVersion, settings);

        if (!settings.Json)
            AnsiConsole.MarkupLine($"Current version: [bold]{currentVersion}[/]");

        // An explicitly named target skips resolution entirely — including the "is it newer?"
        // test, since naming a version is itself the decision. That is what makes --rollback work.
        var explicitTarget = ResolveExplicitTarget(settings, out var targetError);
        if (targetError is not null)
        {
            if (settings.Json)
                Console.WriteLine(JsonSerializer.Serialize(new { current = currentVersion, error = targetError }));
            else
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(targetError)}[/]");
            return 1;
        }

        if (explicitTarget is not null)
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine($"Target version:  [green]{explicitTarget}[/]");

            return await UpdateInPlaceAsync(currentVersion, explicitTarget, settings, cancellation);
        }

        // Check NuGet for the latest version. This also refreshes the on-disk cache that every
        // "new version available" notice reads, so a manual check keeps those surfaces honest.
        var status = await new UpdateChecker().CheckAsync(currentVersion, cancellation);
        var latestVersion = status?.Latest;

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

        // Compared by SemVer, not equality: a locally built or pre-release binary is *ahead* of
        // NuGet's latest, and treating "different" as "outdated" turns an unattended run into a
        // silent downgrade.
        var isUpToDate = !SemanticVersion.IsNewer(currentVersion, latestVersion);

        if (settings.Json && settings.CheckOnly)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                current = currentVersion,
                latest = latestVersion,
                upToDate = isUpToDate,
                publishedAt = status?.LatestPublishedAt,
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

        // Refuse BEFORE stopping anything: once the service and every MCP session are down, a
        // version dotnet tool can't resolve yet reinstalls the old one and still exits 0 (#97).
        if (!status!.IsResolvable)
        {
            var msg = $"v{latestVersion} is on NuGet but not installable yet — NuGet is still indexing it. Nothing was stopped; try again in a few minutes.";
            if (settings.Json)
                Console.WriteLine(JsonSerializer.Serialize(new { current = currentVersion, latest = latestVersion, updated = false, error = msg }));
            else
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(msg)}[/]");
            return 1;
        }

        return await UpdateInPlaceAsync(currentVersion, latestVersion, settings, cancellation);
    }

    /// <summary>
    /// Installs <paramref name="latestVersion"/> from this process and hands the service back on
    /// whatever version is installed afterwards. Only the service is stopped: AI sessions keep their
    /// <c>eidet mcp</c> processes on the old version and switch when they next restart. On Windows
    /// the files those processes (and this one) hold open are moved out of the update's way first
    /// (<see cref="ToolFiles"/>), which is what used to need a trampoline script and a kill of every
    /// eidet process — and a retry loop against MCP clients respawning what was killed.
    /// </summary>
    private static async Task<int> UpdateInPlaceAsync(string currentVersion, string latestVersion,
        Settings settings, CancellationToken cancellation)
    {
        if (!settings.Json)
            AnsiConsole.MarkupLine("[bold]Updating...[/]");

        var restartService = await IsServiceRegisteredAsync(cancellation);
        await StopServiceAsync(settings, cancellation);
        StopStrayServe(settings);

        var toolsDir = ToolFiles.GlobalToolsDir;
        ToolFiles.DeleteLeftovers(toolsDir);
        var moved = OperatingSystem.IsWindows() ? ToolFiles.MoveInUseAside(toolsDir) : [];

        // Verify the install actually advanced the version, and record history from the
        // freshly-installed binary: `dotnet tool update` can exit 0 having installed nothing (#97).
        var (ok, error) = await RunDotnetToolUpdateAsync(latestVersion, settings, cancellation);
        if (ok)
            (ok, error) = await VerifyAndRecordAsync(currentVersion, latestVersion, cancellation);
        if (!ok)
            ToolFiles.Restore(moved);

        // On success and on failure alike: the service was stopped above, and whatever version is
        // installed now must serve rather than leave the host without memory.
        if (restartService)
            await StartServiceAsync(settings, cancellation);

        UpdateLog.Append(ok
            ? $"Updated from v{currentVersion} to v{latestVersion}"
            : $"Update from v{currentVersion} to v{latestVersion} FAILED: {error}");

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                current = currentVersion,
                latest = latestVersion,
                updated = ok,
                upToDate = ok,
                serviceRestarted = restartService,
                error = ok ? null : error,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (ok)
        {
            AnsiConsole.MarkupLine($"[green]Updated to v{latestVersion}[/]");
            if (restartService)
                AnsiConsole.MarkupLine("  Service restarted.");
            AnsiConsole.MarkupLine("  [dim]Open AI sessions switch to the new version when they restart.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Update failed; still on v{currentVersion}:[/] {Markup.Escape(error ?? "Unknown error")}");
        }

        return ok ? 0 : 1;
    }

    /// <summary>
    /// The version the caller named outright, via <c>--to</c> or <c>--rollback</c>, or null when
    /// the target still has to be resolved from NuGet. Returns an error message instead when the
    /// request cannot be honoured — asking to roll back with no recorded predecessor, say.
    /// </summary>
    private static string? ResolveExplicitTarget(Settings settings, out string? error)
    {
        error = null;

        if (settings.Rollback)
        {
            if (settings.To is not null)
            {
                error = "--rollback and --to are mutually exclusive.";
                return null;
            }

            var previous = VersionHistory.GetCurrent()?.PreviousVersion;
            if (string.IsNullOrWhiteSpace(previous))
            {
                error = "No previous version recorded — nothing to roll back to.";
                return null;
            }

            return previous;
        }

        if (string.IsNullOrWhiteSpace(settings.To))
            return null;

        if (!SemanticVersion.TryParse(settings.To, out var parsed))
        {
            error = $"'{settings.To}' is not a version number.";
            return null;
        }

        return parsed.ToString();
    }

    /// <summary>
    /// Stops an <c>eidet serve</c> the service manager didn't start (run by hand), which would
    /// otherwise keep the port and the old version after the restarted service fails to bind.
    /// Identified by the service lock, so <c>eidet mcp</c> processes are left alone.
    /// </summary>
    private static void StopStrayServe(Settings settings)
    {
        if (!ServiceLock.IsServiceRunning(out var info) || info is null || info.Pid == Environment.ProcessId)
            return;
        try
        {
            using var serve = Process.GetProcessById(info.Pid);
            serve.Kill(entireProcessTree: true);
            serve.WaitForExit(5000);
            if (!settings.Json)
                AnsiConsole.MarkupLine($"  Stopped eidet serve (PID {info.Pid})");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// Check whether a Windows scheduled task named "Eidet" is registered.
    /// </summary>
    internal static async Task<bool> IsScheduledTaskRegisteredAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var (exitCode, _) = await RunProcessAsync("schtasks.exe", "/query /tn \"Eidet\" /fo CSV /nh", ct);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check whether any service manager (scheduled task / launchd / systemd) is configured.
    /// </summary>
    internal static async Task<bool> IsServiceRegisteredAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return await IsScheduledTaskRegisteredAsync(ct);

        if (OperatingSystem.IsMacOS())
        {
            var plistPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "LaunchAgents", "dev.eidet.service.plist");
            return File.Exists(plistPath);
        }

        var unitPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user", "eidet.service");
        return File.Exists(unitPath);
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
                        AnsiConsole.MarkupLine("  Stopping scheduled task...");
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
                    AnsiConsole.MarkupLine("  Starting scheduled task...");
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

    private static async Task<(bool Success, string? Error)> RunDotnetToolUpdateAsync(string latestVersion, Settings settings, CancellationToken ct)
    {
        try
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine("  Running dotnet tool update...");

            // Pin the version explicitly so a lagging index can't re-resolve to something else.
            // Pinning does NOT get around registration-index lag: until the registration leaf
            // exists `dotnet tool` reports "not found" yet exits 0 under --ignore-failed-sources,
            // which is why callers gate on UpdateStatus.IsResolvable first and verify after (#97).
            // Ignore failed sources: a private feed in the
            // user's NuGet.Config with an expired token otherwise aborts the whole update,
            // and nuget.org is the only source that can serve eidet anyway.
            var (exitCode, output) = await RunProcessAsync("dotnet", $"tool update -g eidet --version {latestVersion} --ignore-failed-sources", ct);

            if (exitCode == 0)
                return (true, null);

            return (false, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// After a successful `dotnet tool update`, spawns the freshly-installed `eidet`
    /// shim with `--record-installed-from` so the new binary writes its own version
    /// history entry. The new binary refuses to record if its EidetVersion.Current does
    /// not match <paramref name="latestVersion"/>, which catches the "exit 0 but same
    /// version installed" case.
    /// </summary>
    private static async Task<(bool Success, string? Error)> VerifyAndRecordAsync(
        string currentVersion, string latestVersion, CancellationToken ct)
    {
        var args = $"update --record-installed-from {currentVersion} --expected-version {latestVersion}";
        var (exitCode, output) = await RunProcessAsync("eidet", args, ct);

        if (exitCode == 0)
            return (true, null);

        var trimmed = output.Trim();
        if (string.IsNullOrEmpty(trimmed))
            trimmed = $"freshly-installed eidet did not report v{latestVersion} (dotnet tool update may have silently re-resolved to the previous version)";
        return (false, trimmed);
    }

    /// <summary>
    /// Implementation of the hidden `--record-installed-from` callback. Verifies that
    /// the running binary's EidetVersion.Current matches <c>--expected-version</c> (when
    /// supplied) and records a version history entry. Returns non-zero on mismatch so
    /// callers can surface the silent-no-op failure mode.
    /// </summary>
    private static int RecordInstalledVersion(string currentVersion, Settings settings)
    {
        var previous = settings.RecordInstalledFrom!;
        var expected = settings.ExpectedVersion;

        if (expected is not null && !string.Equals(currentVersion, expected, StringComparison.OrdinalIgnoreCase))
        {
            var msg = $"Installed binary reports v{currentVersion} but expected v{expected}. " +
                      "dotnet tool update likely no-op'd (NuGet search-index lag). Not recording version history.";
            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    current = currentVersion,
                    expected,
                    previous,
                    recorded = false,
                    error = msg,
                }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(msg)}[/]");
            }
            return 1;
        }

        VersionHistory.Record(currentVersion, previous, "dotnet-tool-update");

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                current = currentVersion,
                previous,
                recorded = true,
            }));
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]Recorded v{currentVersion} in version history[/] (from v{previous}).");
        }
        return 0;
    }

    internal static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
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
