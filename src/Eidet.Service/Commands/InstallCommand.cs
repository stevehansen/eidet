using System.Runtime.InteropServices;
using Eidet.Core;
using Eidet.Core.Services;
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
        if (!settings.Json)
            AnsiConsole.MarkupLine("[bold]Installing Eidet service...[/]");

        // Resolve the dotnet tool shim path
        var shimPath = GetDotnetToolShimPath();
        if (shimPath == null || !File.Exists(shimPath))
        {
            if (settings.Json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    installed = false,
                    error = "Could not find eidet dotnet tool shim. Install first: dotnet tool install -g eidet",
                }));
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Could not find eidet dotnet tool.[/]");
                AnsiConsole.MarkupLine("Install first: [dim]dotnet tool install -g eidet[/]");
            }
            return 1;
        }

        if (!settings.Json)
            AnsiConsole.MarkupLine($"  Tool path: [green]{Markup.Escape(shimPath)}[/]");

        // Register as system service
        string serviceResult;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            serviceResult = await RegisterWindowsServiceAsync(shimPath, cancellation);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            serviceResult = await RegisterLaunchdAsync(shimPath, cancellation);
        else
            serviceResult = await RegisterSystemdAsync(shimPath, cancellation);

        // Auto-configure MCP for Claude Code / Claude Desktop
        var mcpResult = ConfigureMcpClients(shimPath);

        // Record in version history
        VersionHistory.Record(EidetVersion.Current, null, "dotnet-tool-install");

        if (settings.Json)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                installed = true, shimPath, service = serviceResult, mcpConfigured = mcpResult
            });
            Console.WriteLine(json);
        }
        else
        {
            AnsiConsole.MarkupLine($"  Service: [green]{Markup.Escape(serviceResult)}[/]");
            if (mcpResult != null)
                AnsiConsole.MarkupLine($"  MCP:     [green]{Markup.Escape(mcpResult)}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Run [bold]eidet setup[/] if this is a first-time install.");
        }

        return 0;
    }

    internal static string? GetDotnetToolShimPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shimName = OperatingSystem.IsWindows() ? "eidet.exe" : "eidet";
        var shimPath = Path.Combine(home, ".dotnet", "tools", shimName);
        return File.Exists(shimPath) ? shimPath : null;
    }

    private static async Task<string> RegisterWindowsServiceAsync(string shimPath, CancellationToken ct)
    {
        var taskName = "Eidet";

        // Remove existing task if present
        await RunProcessAsync("schtasks.exe", $"/delete /tn \"{taskName}\" /f", ct);

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
                  <Count>999</Count>
                </RestartOnFailure>
              </Settings>
              <Actions>
                <Exec>
                  <Command>{shimPath}</Command>
                  <Arguments>serve</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var xmlPath = Path.Combine(Path.GetTempPath(), "eidet-task.xml");
        await File.WriteAllTextAsync(xmlPath, xml, ct);

        var createResult = await RunProcessAsync("schtasks.exe",
            $"/create /tn \"{taskName}\" /xml \"{xmlPath}\" /f", ct);

        File.Delete(xmlPath);

        if (createResult.ExitCode != 0)
            return $"Failed to create scheduled task: {createResult.Output}";

        var startResult = await RunProcessAsync("schtasks.exe", $"/run /tn \"{taskName}\"", ct);
        return startResult.ExitCode == 0
            ? "Scheduled task registered and started (runs at logon)"
            : "Scheduled task registered (starts at next logon)";
    }

    private static async Task<string> RegisterLaunchdAsync(string shimPath, CancellationToken ct)
    {
        var plistDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");
        Directory.CreateDirectory(plistDir);

        var plistPath = Path.Combine(plistDir, "dev.eidet.service.plist");
        var logDir = GetLogDir();
        Directory.CreateDirectory(logDir);

        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>dev.eidet.service</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{shimPath}</string>
                    <string>serve</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>StandardOutPath</key>
                <string>{logDir}/eidet.log</string>
                <key>StandardErrorPath</key>
                <string>{logDir}/eidet.err</string>
            </dict>
            </plist>
            """;

        await File.WriteAllTextAsync(plistPath, plist, ct);

        await RunProcessAsync("launchctl", $"unload {plistPath}", ct);
        var loadResult = await RunProcessAsync("launchctl", $"load {plistPath}", ct);
        return loadResult.ExitCode == 0
            ? "launchd agent registered and loaded"
            : $"launchd plist written to {plistPath} (load manually)";
    }

    private static async Task<string> RegisterSystemdAsync(string shimPath, CancellationToken ct)
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
            ExecStart={shimPath} serve
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

    internal static string? ConfigureMcpClients(string shimPath)
    {
        var configured = new List<string>();

        // Claude Code: ~/.claude/settings.json (mcpServers key)
        var claudeCodeResult = ConfigureClaudeCodeMcp(shimPath);
        if (claudeCodeResult) configured.Add("Claude Code");

        // Claude Desktop: platform-specific config
        var claudeDesktopConfig = GetClaudeDesktopConfigPath();
        if (claudeDesktopConfig != null)
        {
            var result = ConfigureMcpJson(claudeDesktopConfig, shimPath);
            if (result) configured.Add("Claude Desktop");
        }

        return configured.Count > 0 ? string.Join(", ", configured) : null;
    }

    private static bool ConfigureClaudeCodeMcp(string shimPath)
    {
        try
        {
            // Claude Code stores MCP servers in ~/.claude.json (user config)
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var claudeJsonPath = Path.Combine(home, ".claude.json");

            if (!File.Exists(claudeJsonPath))
                return false;

            var existing = File.ReadAllText(claudeJsonPath);
            var root = System.Text.Json.Nodes.JsonNode.Parse(existing)?.AsObject();
            if (root is null)
                return false;

            if (!root.ContainsKey("mcpServers"))
                root["mcpServers"] = new System.Text.Json.Nodes.JsonObject();

            var servers = root["mcpServers"]!.AsObject();
            if (servers.ContainsKey("eidet"))
                return false;

            // Use just "eidet" — the dotnet tool shim is on PATH
            servers["eidet"] = new System.Text.Json.Nodes.JsonObject
            {
                ["command"] = "eidet",
                ["args"] = new System.Text.Json.Nodes.JsonArray("mcp"),
            };

            var json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(claudeJsonPath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ConfigureMcpJson(string configPath, string shimPath)
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

            if (!root.ContainsKey("mcpServers"))
                root["mcpServers"] = new System.Text.Json.Nodes.JsonObject();

            var servers = root["mcpServers"]!.AsObject();
            if (servers.ContainsKey("eidet"))
                return false;

            // Use just "eidet" — the dotnet tool shim is on PATH
            servers["eidet"] = new System.Text.Json.Nodes.JsonObject
            {
                ["command"] = "eidet",
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
            return null;
        }

        return Directory.Exists(configDir)
            ? Path.Combine(configDir, "claude_desktop_config.json")
            : null;
    }

    internal static string GetLogDir()
    {
        if (OperatingSystem.IsWindows())
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
