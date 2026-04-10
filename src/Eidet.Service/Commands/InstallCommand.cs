using System.Runtime.InteropServices;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path");
        var installDir = GetInstallDir();

        if (!settings.Json)
            AnsiConsole.MarkupLine("[bold]Installing Eidet service...[/]");

        // Step 1: Copy binary to well-known location
        Directory.CreateDirectory(installDir);
        var targetExe = Path.Combine(installDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "eidet.exe" : "eidet");

        if (!string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(exePath, targetExe, overwrite: true);
            if (!settings.Json)
                AnsiConsole.MarkupLine($"  Binary: [green]Copied[/] to {Markup.Escape(installDir)}");
        }
        else
        {
            if (!settings.Json)
                AnsiConsole.MarkupLine($"  Binary: [green]Already in place[/]");
        }

        // Step 2: Register as system service
        string result;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            result = await RegisterWindowsServiceAsync(targetExe, cancellation);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            result = await RegisterLaunchdAsync(targetExe, cancellation);
        else
            result = await RegisterSystemdAsync(targetExe, cancellation);

        if (settings.Json)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                installed = true, exePath = targetExe, service = result
            });
            Console.WriteLine(json);
        }
        else
        {
            AnsiConsole.MarkupLine($"  Service: [green]{Markup.Escape(result)}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Run [bold]eidet setup[/] if this is a first-time install.");
        }

        return 0;
    }

    private static async Task<string> RegisterWindowsServiceAsync(string exePath, CancellationToken ct)
    {
        var serviceName = "Eidet";
        // Check if service already exists
        var checkResult = await RunProcessAsync("sc.exe", $"query {serviceName}", ct);
        if (checkResult.ExitCode == 0)
        {
            // Stop and delete existing service, then recreate
            await RunProcessAsync("sc.exe", $"stop {serviceName}", ct);
            await Task.Delay(1000, ct);
            await RunProcessAsync("sc.exe", $"delete {serviceName}", ct);
            await Task.Delay(500, ct);
        }

        var createResult = await RunProcessAsync("sc.exe",
            $"create {serviceName} binPath= \"\\\"{exePath}\\\" serve\" start= auto DisplayName= \"Eidet Memory Service\"", ct);

        if (createResult.ExitCode != 0)
            return $"Failed to create service: {createResult.Output}";

        // Set description
        await RunProcessAsync("sc.exe",
            $"description {serviceName} \"Long-term memory for AI coding agents\"", ct);

        // Start the service
        var startResult = await RunProcessAsync("sc.exe", $"start {serviceName}", ct);
        return startResult.ExitCode == 0
            ? "Windows Service registered and started"
            : "Windows Service registered (start manually with: sc.exe start Eidet)";
    }

    private static async Task<string> RegisterLaunchdAsync(string exePath, CancellationToken ct)
    {
        var plistDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");
        Directory.CreateDirectory(plistDir);

        var plistPath = Path.Combine(plistDir, "dev.eidet.service.plist");
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>dev.eidet.service</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{exePath}</string>
                    <string>serve</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>StandardOutPath</key>
                <string>{GetLogDir()}/eidet.log</string>
                <key>StandardErrorPath</key>
                <string>{GetLogDir()}/eidet.err</string>
            </dict>
            </plist>
            """;

        await File.WriteAllTextAsync(plistPath, plist, ct);

        // Unload if already loaded, then load
        await RunProcessAsync("launchctl", $"unload {plistPath}", ct);
        var loadResult = await RunProcessAsync("launchctl", $"load {plistPath}", ct);
        return loadResult.ExitCode == 0
            ? "launchd agent registered and loaded"
            : $"launchd plist written to {plistPath} (load manually)";
    }

    private static async Task<string> RegisterSystemdAsync(string exePath, CancellationToken ct)
    {
        var unitDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user");
        Directory.CreateDirectory(unitDir);

        var unitPath = Path.Combine(unitDir, "eidet.service");
        var unit = $"""
            [Unit]
            Description=Eidet Memory Service
            After=network.target

            [Service]
            Type=simple
            ExecStart={exePath} serve
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=default.target
            """;

        await File.WriteAllTextAsync(unitPath, unit, ct);

        await RunProcessAsync("systemctl", "--user daemon-reload", ct);
        await RunProcessAsync("systemctl", "--user enable eidet.service", ct);
        var startResult = await RunProcessAsync("systemctl", "--user start eidet.service", ct);
        return startResult.ExitCode == 0
            ? "systemd user service registered, enabled, and started"
            : $"systemd unit written to {unitPath} (start manually with: systemctl --user start eidet)";
    }

    internal static string GetInstallDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Eidet", "bin");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".eidet", "bin");
    }

    internal static string GetLogDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Eidet", "logs");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".eidet", "logs");
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
