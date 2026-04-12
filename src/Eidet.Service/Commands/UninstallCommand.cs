using System.Runtime.InteropServices;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class UninstallCommand : AsyncCommand<UninstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--purge")]
        public bool Purge { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!settings.Json)
            AnsiConsole.MarkupLine("[bold]Uninstalling Eidet service...[/]");

        string result;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            result = await UnregisterWindowsServiceAsync(cancellation);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            result = await UnregisterLaunchdAsync(cancellation);
        else
            result = await UnregisterSystemdAsync(cancellation);

        if (!settings.Json)
            AnsiConsole.MarkupLine($"  Service: [green]{Markup.Escape(result)}[/]");

        // Note: dotnet tool binary is managed by 'dotnet tool uninstall -g eidet'
        if (!settings.Json)
            AnsiConsole.MarkupLine("  Binary: [dim]Run 'dotnet tool uninstall -g eidet' to remove the tool[/]");

        if (settings.Purge)
        {
            var dataDir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Eidet")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".eidet");

            if (Directory.Exists(dataDir))
            {
                try
                {
                    Directory.Delete(dataDir, recursive: true);
                    if (!settings.Json)
                        AnsiConsole.MarkupLine($"  Data: [red]Purged[/] ({Markup.Escape(dataDir)})");
                }
                catch (Exception ex)
                {
                    if (!settings.Json)
                        AnsiConsole.MarkupLine($"  Data: [yellow]Could not purge: {Markup.Escape(ex.Message)}[/]");
                }
            }
        }
        else if (!settings.Json)
        {
            AnsiConsole.MarkupLine("  Data: [dim]Preserved (use --purge to delete)[/]");
        }

        if (settings.Json)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                uninstalled = true, purged = settings.Purge
            });
            Console.WriteLine(json);
        }

        return 0;
    }

    private static async Task<string> UnregisterWindowsServiceAsync(CancellationToken ct)
    {
        // Stop the running task
        await RunProcessAsync("schtasks.exe", "/end /tn \"Eidet\"", ct);

        // Delete the scheduled task
        var result = await RunProcessAsync("schtasks.exe", "/delete /tn \"Eidet\" /f", ct);

        // Also clean up legacy Windows Service if present
        await RunProcessAsync("sc.exe", "stop Eidet", ct);
        await RunProcessAsync("sc.exe", "delete Eidet", ct);

        return result.ExitCode == 0 ? "Scheduled task removed" : "Task not found or already removed";
    }

    private static async Task<string> UnregisterLaunchdAsync(CancellationToken ct)
    {
        var plistPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", "dev.eidet.service.plist");

        if (File.Exists(plistPath))
        {
            await RunProcessAsync("launchctl", $"unload {plistPath}", ct);
            File.Delete(plistPath);
            return "launchd agent unloaded and removed";
        }
        return "launchd agent not found";
    }

    private static async Task<string> UnregisterSystemdAsync(CancellationToken ct)
    {
        await RunProcessAsync("systemctl", "--user stop eidet.service", ct);
        await RunProcessAsync("systemctl", "--user disable eidet.service", ct);

        var unitPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user", "eidet.service");

        if (File.Exists(unitPath))
        {
            File.Delete(unitPath);
            await RunProcessAsync("systemctl", "--user daemon-reload", ct);
            return "systemd user service stopped, disabled, and removed";
        }
        return "systemd unit not found";
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
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
