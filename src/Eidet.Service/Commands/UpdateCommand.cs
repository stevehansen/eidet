using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Eidet.Core;
using Eidet.Core.Configuration;
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

        // Hidden flag invoked by the freshly-installed binary (direct path or trampoline
        // script) to record version history *after* dotnet tool update has actually
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

    private const string NuGetPackageId = "eidet";
    private const string NuGetIndexUrl = $"https://api.nuget.org/v3-flatcontainer/{NuGetPackageId}/index.json";

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var currentVersion = EidetVersion.Current;

        // Post-install callback: record version history from the freshly-installed binary.
        // This is invoked by the trampoline script (Windows) or by UpdateDirectAsync after
        // a successful dotnet tool update. It deliberately bypasses the NuGet check.
        if (settings.RecordInstalledFrom is not null)
            return RecordInstalledVersion(currentVersion, settings);

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

        // On Windows, the running process locks its own DLLs, so dotnet tool update
        // will fail with "Access denied". We use a trampoline: stop everything, write
        // a temp script that does the actual update after we exit, then exit immediately.
        if (OperatingSystem.IsWindows())
            return await UpdateViaTrampolineAsync(currentVersion, latestVersion, settings, cancellation);

        return await UpdateDirectAsync(currentVersion, latestVersion, settings, cancellation);
    }

    /// <summary>
    /// Direct update for macOS/Linux where loaded DLLs don't hold file locks.
    /// </summary>
    private static async Task<int> UpdateDirectAsync(string currentVersion, string latestVersion,
        Settings settings, CancellationToken cancellation)
    {
        if (!settings.Json)
            AnsiConsole.MarkupLine("[bold]Updating...[/]");

        // Step 1: Stop the service
        await StopServiceAsync(settings, cancellation);

        // Step 2: Kill other eidet processes (mcp, etc.) that may hold file locks
        KillOtherEidetProcesses(settings);

        // Always restart after update if a service is registered
        var restartService = await IsServiceRegisteredAsync(cancellation);

        // Step 3: Run dotnet tool update (pinned to the resolved latest version so we
        // bypass the NuGet search-index lag that can otherwise silently no-op).
        var updateResult = await RunDotnetToolUpdateAsync(latestVersion, settings, cancellation);

        if (!updateResult.Success)
        {
            if (restartService)
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

        // Step 4: Verify the install actually advanced the version, and record history
        // from the freshly-installed binary. Spawning `eidet` invokes the global shim
        // which now points at the new binary; if it reports a different version than
        // expected, the install silently no-op'd and we treat it as a failure.
        var verify = await VerifyAndRecordAsync(currentVersion, latestVersion, cancellation);
        if (!verify.Success)
        {
            if (restartService)
                await StartServiceAsync(settings, cancellation);

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    current = currentVersion,
                    latest = latestVersion,
                    updated = false,
                    error = verify.Error,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Update failed:[/] {Markup.Escape(verify.Error ?? "Version did not advance")}");
            }
            return 1;
        }

        // Step 5: Restart service
        if (restartService)
            await StartServiceAsync(settings, cancellation);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                current = currentVersion,
                latest = latestVersion,
                updated = true,
                upToDate = true,
                serviceRestarted = restartService,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]Updated to v{latestVersion}[/]");
            if (restartService)
                AnsiConsole.MarkupLine("  Service restarted.");
        }

        return 0;
    }

    /// <summary>
    /// Windows trampoline update: generates a script that runs after this process exits,
    /// because Windows locks loaded DLLs and dotnet can't replace them while we're running.
    /// </summary>
    private static async Task<int> UpdateViaTrampolineAsync(string currentVersion, string latestVersion,
        Settings settings, CancellationToken cancellation)
    {
        if (!settings.Json)
            AnsiConsole.MarkupLine("[bold]Updating...[/]");

        // Step 1: Stop the scheduled task / service
        await StopServiceAsync(settings, cancellation);

        // Step 2: Kill all OTHER eidet processes (mcp, serve) — not ourselves
        KillOtherEidetProcesses(settings);

        // Always restart after update — the service should be running
        var restartService = await IsServiceRegisteredAsync(cancellation);

        // Step 3: Generate and launch the trampoline script. The script now records
        // version history *after* a successful install by invoking the freshly-installed
        // `eidet update --record-installed-from ...`, so a failed/no-op update can no
        // longer leave a bogus history entry.
        var scriptPath = GenerateWindowsTrampolineScript(currentVersion, latestVersion, restartService);

        if (!settings.Json)
        {
            AnsiConsole.MarkupLine("  Launching update script...");
            AnsiConsole.MarkupLine($"  [dim](script: {Markup.Escape(scriptPath)})[/]");
        }

        LaunchDetachedScript(scriptPath);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                current = currentVersion,
                latest = latestVersion,
                trampolineScript = scriptPath,
                serviceWillRestart = restartService,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            AnsiConsole.MarkupLine("  Exiting to release file locks...");
            AnsiConsole.MarkupLine($"  The update script will install v{latestVersion} and restart the service.");
            AnsiConsole.MarkupLine("  Check [dim]eidet status[/] in a few seconds to verify.");
        }

        // Exit immediately so our DLLs are unlocked
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

    /// <summary>
    /// Kill all eidet processes except the current one (handles mcp, serve, etc.).
    /// Returns the count of processes killed.
    /// </summary>
    internal static int KillOtherEidetProcesses(Settings? settings = null)
    {
        var currentPid = Environment.ProcessId;
        var killed = 0;

        try
        {
            // The dotnet tool shim creates processes named "eidet"; the actual runtime
            // process may also appear as "dotnet" running eidet.dll. We handle the eidet.exe
            // shim here and the serve/mcp lock file separately.
            var all = Process.GetProcessesByName("eidet");
            var killable = SelectProcessesToKill(all.Select(p => p.Id), currentPid).ToHashSet();

            foreach (var proc in all)
            {
                try
                {
                    if (killable.Contains(proc.Id) && !proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                        killed++;
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }

            if (killed > 0 && settings is { Json: false })
                AnsiConsole.MarkupLine($"  Stopped {killed} eidet process{(killed == 1 ? "" : "es")} (mcp/serve)");
        }
        catch { }

        return killed;
    }

    /// <summary>
    /// Pure filter: from a set of candidate PIDs, return the ones we'd kill —
    /// everything except the caller's own PID. Extracted so the selection logic
    /// can be unit-tested without touching real OS processes.
    /// </summary>
    internal static IEnumerable<int> SelectProcessesToKill(IEnumerable<int> candidatePids, int currentPid)
        => candidatePids.Where(pid => pid != currentPid);

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
    private static async Task<bool> IsServiceRegisteredAsync(CancellationToken ct)
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

    /// <summary>
    /// Generate a Windows .cmd script that performs the actual update after this process exits.
    /// The script waits for our PID to exit, runs dotnet tool update, and restarts the service.
    /// </summary>
    internal static string GenerateWindowsTrampolineScript(string currentVersion, string latestVersion, bool restartService)
    {
        var myPid = Environment.ProcessId;
        var configDir = ConfigManager.GetConfigDir();
        var scriptPath = Path.Combine(Path.GetTempPath(), $"eidet-update-{Guid.NewGuid():N}.cmd");
        var logPath = Path.Combine(configDir, "update.log");

        // The script:
        // 1. Waits for the calling process to exit (polls every second, up to 30s)
        // 2. Kills any remaining eidet processes (belt and suspenders)
        // 3. Runs dotnet tool update
        // 4. Restarts the scheduled task if it was running
        // 5. Writes a brief log file
        // 6. Cleans itself up
        var script = $"""
            @echo off
            setlocal
            echo Eidet update trampoline — updating v{currentVersion} to v{latestVersion}
            echo Waiting for PID {myPid} to exit...

            REM Wait for the calling eidet process to exit (up to 30 seconds)
            set /a TRIES=0
            :WAIT_LOOP
            tasklist /fi "PID eq {myPid}" 2>nul | find "{myPid}" >nul
            if errorlevel 1 goto DONE_WAITING
            set /a TRIES+=1
            if %TRIES% geq 30 (
                echo WARNING: PID {myPid} did not exit after 30 seconds, proceeding anyway
                goto DONE_WAITING
            )
            timeout /t 1 /nobreak >nul
            goto WAIT_LOOP
            :DONE_WAITING

            REM Kill any remaining eidet processes (mcp, serve, etc.)
            echo Killing remaining eidet processes...
            taskkill /f /im eidet.exe 2>nul
            timeout /t 2 /nobreak >nul

            REM Run the actual update — pin the version so dotnet falls back to the
            REM NuGet flat container instead of the search index (which lags publish).
            echo Running dotnet tool update...
            dotnet tool update -g eidet --version {latestVersion}
            if errorlevel 1 (
                echo UPDATE FAILED >> "{logPath}"
                echo %date% %time% - Update from v{currentVersion} to v{latestVersion} FAILED >> "{logPath}"
                echo dotnet tool update -g eidet --version {latestVersion} returned error >> "{logPath}"
                goto CLEANUP
            )

            REM Verify the install actually advanced the version and record history from
            REM the freshly-installed binary. If the binary reports a different version,
            REM eidet update --record-installed-from exits non-zero and we log the failure.
            echo Verifying installed version and recording history...
            eidet update --record-installed-from {currentVersion} --expected-version {latestVersion}
            if errorlevel 1 (
                echo VERIFY FAILED >> "{logPath}"
                echo %date% %time% - Update from v{currentVersion} to v{latestVersion} could not be verified >> "{logPath}"
                echo Installed binary did not report v{latestVersion} — dotnet tool update may have silently re-resolved. >> "{logPath}"
                goto CLEANUP
            )

            echo %date% %time% - Updated from v{currentVersion} to v{latestVersion} >> "{logPath}"
            echo Update successful.
            {(restartService ? """
            REM Restart the scheduled task
            echo Restarting Eidet service...
            schtasks.exe /run /tn "Eidet"
            if errorlevel 1 (
                echo WARNING: Could not restart scheduled task. Run: schtasks /run /tn "Eidet"
                echo %date% %time% - Service restart FAILED >> "{logPath}"
            ) else (
                echo Service restarted.
                echo %date% %time% - Service restarted >> "{logPath}"
            )
            """ : $"""
            echo %date% %time% - Service was not running, skipping restart >> "{logPath}"
            """)}
            :CLEANUP
            REM Clean up this script
            (goto) 2>nul & del "%~f0"
            """;

        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    /// <summary>
    /// Launch a script in a fully detached process (no parent relationship).
    /// </summary>
    private static void LaunchDetachedScript(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        var proc = Process.Start(psi);
        // Don't wait — let it run after we exit
        proc?.Dispose();
    }

    private static async Task<(bool Success, string? Error)> RunDotnetToolUpdateAsync(string latestVersion, Settings settings, CancellationToken ct)
    {
        try
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine("  Running dotnet tool update...");

            // Pin the version explicitly: `dotnet tool` falls back to the NuGet flat
            // container when an exact version is requested, sidestepping the search-index
            // lag (10–30 min after publish) that otherwise causes a silent re-resolve to
            // the previously installed version.
            var (exitCode, output) = await RunProcessAsync("dotnet", $"tool update -g eidet --version {latestVersion}", ct);

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
