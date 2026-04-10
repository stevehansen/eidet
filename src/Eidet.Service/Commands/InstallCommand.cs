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

        // Step 3: Auto-configure MCP for Claude Code / Claude Desktop
        var mcpResult = ConfigureMcpClients(targetExe);

        if (settings.Json)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                installed = true, exePath = targetExe, service = result, mcpConfigured = mcpResult
            });
            Console.WriteLine(json);
        }
        else
        {
            AnsiConsole.MarkupLine($"  Service: [green]{Markup.Escape(result)}[/]");
            if (mcpResult != null)
                AnsiConsole.MarkupLine($"  MCP:     [green]{Markup.Escape(mcpResult)}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Run [bold]eidet setup[/] if this is a first-time install.");
        }

        return 0;
    }

    private static async Task<string> RegisterWindowsServiceAsync(string exePath, CancellationToken ct)
    {
        var taskName = "Eidet";

        // Remove existing task if present
        await RunProcessAsync("schtasks.exe", $"/delete /tn \"{taskName}\" /f", ct);

        // Write task XML to temp file
        var xmlPath = Path.Combine(Path.GetTempPath(), "eidet-task.xml");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Eidet Memory Service — long-term memory for AI coding agents</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions>
                <Exec>
                  <Command>{exePath}</Command>
                  <Arguments>serve</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        await File.WriteAllTextAsync(xmlPath, xml, ct);

        var createResult = await RunProcessAsync("schtasks.exe",
            $"/create /tn \"{taskName}\" /xml \"{xmlPath}\" /f", ct);

        File.Delete(xmlPath);

        if (createResult.ExitCode != 0)
            return $"Failed to create scheduled task: {createResult.Output}";

        // Start immediately
        var startResult = await RunProcessAsync("schtasks.exe", $"/run /tn \"{taskName}\"", ct);
        return startResult.ExitCode == 0
            ? "Scheduled task registered and started (runs at logon)"
            : "Scheduled task registered (starts at next logon)";
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

    internal static string? ConfigureMcpClients(string exePath)
    {
        var configured = new List<string>();

        // Claude Code: ~/.claude/claude_desktop_config.json (Windows) or similar
        var claudeCodeConfig = GetClaudeCodeMcpPath();
        if (claudeCodeConfig != null)
        {
            var result = ConfigureMcpJson(claudeCodeConfig, exePath);
            if (result) configured.Add("Claude Code");
        }

        // Claude Desktop
        var claudeDesktopConfig = GetClaudeDesktopConfigPath();
        if (claudeDesktopConfig != null)
        {
            var result = ConfigureMcpJson(claudeDesktopConfig, exePath);
            if (result) configured.Add("Claude Desktop");
        }

        return configured.Count > 0 ? string.Join(", ", configured) : null;
    }

    private static bool ConfigureMcpJson(string configPath, string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(configPath)!;
            Directory.CreateDirectory(dir);

            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(configPath))
            {
                var existing = File.ReadAllText(configPath);
                root = System.Text.Json.Nodes.JsonNode.Parse(existing)?.AsObject()
                    ?? new System.Text.Json.Nodes.JsonObject();
            }
            else
            {
                root = new System.Text.Json.Nodes.JsonObject();
            }

            // Ensure mcpServers key exists
            if (!root.ContainsKey("mcpServers"))
                root["mcpServers"] = new System.Text.Json.Nodes.JsonObject();

            var servers = root["mcpServers"]!.AsObject();

            // Only add if not already configured
            if (servers.ContainsKey("eidet"))
                return false;

            servers["eidet"] = new System.Text.Json.Nodes.JsonObject
            {
                ["command"] = exePath,
                ["args"] = new System.Text.Json.Nodes.JsonArray("mcp"),
            };

            var json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetClaudeCodeMcpPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".claude", "claude_desktop_config.json");
        // Claude Code uses ~/.claude/ directory — check if it exists
        var claudeDir = Path.Combine(home, ".claude");
        return Directory.Exists(claudeDir) ? path : null;
    }

    private static string? GetClaudeDesktopConfigPath()
    {
        string configDir;
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            configDir = Path.Combine(appData, "Claude");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configDir = Path.Combine(home, "Library", "Application Support", "Claude");
        }
        else
        {
            return null; // No Claude Desktop on Linux
        }

        return Directory.Exists(configDir)
            ? Path.Combine(configDir, "claude_desktop_config.json")
            : null;
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
