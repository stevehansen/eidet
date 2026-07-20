using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Mcp;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class StatusCommand : AsyncCommand<StatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();

        string? ravenVersion = null;
        long? docCount = null;
        UnenrichedStats? backlog = null;

        try
        {
            using var store = DocumentStoreFactory.CreateFromConfig(config);
            var ravenStore = new RavenEidetStore(store);
            var info = await ravenStore.GetDatabaseInfoAsync();

            if (info != null)
            {
                ravenVersion = info.ServerVersion;
                docCount = info.DocumentCount;
            }

            if (config.Enrichment.Enabled)
                backlog = await ravenStore.GetUnenrichedStatsAsync(ct: cancellation);
        }
        catch { }

        // Service status (lock file + health check)
        var (serviceRunning, serviceHealthy, serviceInfo) = await ServiceLock.CheckHealthAsync(cancellation);

        // Version history
        var versionHistory = VersionHistory.Load();
        var lastInstall = versionHistory.Count > 0 ? versionHistory[^1] : null;

        // Check for update (non-blocking, best-effort)
        string? latestVersion = null;
        try
        {
            latestVersion = await UpdateCommand.GetLatestNuGetVersionAsync(cancellation);
        }
        catch { }

        // MCP client registration (best-effort).
        var mcpClients = new List<(string Name, McpInstallStatus Status)>();
        foreach (var client in McpClientRegistry.All)
        {
            try { mcpClients.Add((client.Name, await client.CheckAsync(cancellation))); }
            catch { mcpClients.Add((client.Name, McpInstallStatus.NotAvailable)); }
        }

        var currentVersion = Eidet.Core.EidetVersion.Current;
        var updateAvailable = latestVersion != null
            && !string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase);

        if (settings.Json)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                version = currentVersion,
                latestVersion,
                updateAvailable,
                installedAt = lastInstall?.InstalledAt,
                service = new
                {
                    running = serviceRunning,
                    healthy = serviceHealthy,
                    pid = serviceInfo?.Pid,
                    port = serviceInfo?.Port,
                    bind = serviceInfo?.BindAddress,
                    startedAt = serviceInfo?.StartedAt,
                },
                storage = new
                {
                    mode = config.Storage.Mode,
                    url = config.Storage.RavenUrl,
                    database = config.Storage.DatabaseName,
                    serverVersion = ravenVersion,
                    documents = docCount,
                },
                enrichment = new
                {
                    enabled = config.Enrichment.Enabled,
                    provider = config.Enrichment.Provider.ToString(),
                    url = config.Enrichment.Url,
                    unenriched = backlog?.Count,
                    oldestUnenriched = backlog?.OldestCreatedAt,
                },
                versionHistory = versionHistory.TakeLast(5).Select(e => new
                {
                    e.Version,
                    e.InstalledAt,
                    e.PreviousVersion,
                    e.Source,
                }),
                mcpClients = mcpClients.Select(c => new { name = c.Name, status = c.Status.ToString() }),
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            Console.WriteLine(json);
        }
        else
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]Eidet[/] v{currentVersion}");

            if (updateAvailable)
                AnsiConsole.MarkupLine($"  [yellow]Update available: v{latestVersion}[/] — run [dim]eidet update[/]");

            if (lastInstall != null)
                AnsiConsole.MarkupLine($"  Installed: {lastInstall.InstalledAt.LocalDateTime:yyyy-MM-dd HH:mm} via {lastInstall.Source}");

            // Service status
            if (serviceRunning && serviceHealthy)
            {
                var uptime = DateTimeOffset.UtcNow - serviceInfo!.StartedAt;
                var uptimeStr = uptime.TotalHours >= 1
                    ? $"{uptime.TotalHours:F0}h {uptime.Minutes}m"
                    : $"{uptime.TotalMinutes:F0}m";
                AnsiConsole.MarkupLine($"  Service:    [green]Running[/] (PID {serviceInfo.Pid}, port {serviceInfo.Port}, uptime {uptimeStr})");
            }
            else if (serviceRunning)
                AnsiConsole.MarkupLine($"  Service:    [yellow]Running but not responding[/] (PID {serviceInfo!.Pid}, port {serviceInfo.Port})");
            else
            {
                var restartHint = await GetRestartHintAsync(cancellation);
                AnsiConsole.MarkupLine($"  Service:    [dim]Not running[/] — start with [dim]{restartHint}[/]");
            }

            AnsiConsole.MarkupLine($"  Storage:    {config.Storage.Mode} RavenDB at [link]{config.Storage.RavenUrl}[/]");
            AnsiConsole.MarkupLine($"  Database:   {config.Storage.DatabaseName}" +
                (docCount.HasValue ? $" ({docCount} documents)" : " [dim](unreachable)[/]"));

            if (ravenVersion != null)
                AnsiConsole.MarkupLine($"  RavenDB:    v{ravenVersion}");

            AnsiConsole.MarkupLine($"  Enrichment: {(config.Enrichment.Enabled ? $"{config.Enrichment.Provider} @ {config.Enrichment.Url}{FormatBacklog(backlog)}" : "[dim]Disabled[/]")}");
            AnsiConsole.MarkupLine($"  Config:     {ConfigManager.GetConfigPath()}");
            AnsiConsole.MarkupLine($"  Logs:       {Path.Combine(ConfigManager.GetConfigDir(), "logs", "eidet.log")}");
            AnsiConsole.MarkupLine($"  MCP:        {FormatMcpClients(mcpClients)}");

            // Show recent version history
            if (versionHistory.Count > 1)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Version history[/] (recent):");
                foreach (var entry in versionHistory.TakeLast(5).Reverse())
                {
                    var from = entry.PreviousVersion != null ? $" (from v{entry.PreviousVersion})" : "";
                    AnsiConsole.MarkupLine($"  v{entry.Version} — {entry.InstalledAt.LocalDateTime:yyyy-MM-dd HH:mm}{from}");
                }
            }

            AnsiConsole.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// Enrichment backlog suffix: nothing when unknown or empty; count + age of the oldest
    /// unenriched memory otherwise. An oldest that keeps aging across runs means a stuck doc.
    /// </summary>
    private static string FormatBacklog(UnenrichedStats? backlog)
    {
        if (backlog is not { Count: > 0 }) return "";
        var age = backlog.OldestCreatedAt is { } oldest
            ? $", oldest {Math.Max(0, (DateTime.UtcNow - oldest).TotalDays):F0}d"
            : "";
        return $" — [yellow]{backlog.Count} unenriched{age}[/]";
    }

    /// <summary>
    /// Compact MCP-client summary for status output: configured ones get
    /// ✓, installed-but-not-configured get a yellow ✗, missing tools are
    /// listed as dim "(not installed)" at the end (or hidden if all
    /// detected clients are configured).
    /// </summary>
    private static string FormatMcpClients(IReadOnlyList<(string Name, McpInstallStatus Status)> clients)
    {
        var configured = clients.Where(c => c.Status == McpInstallStatus.Configured).ToList();
        var pending = clients.Where(c => c.Status == McpInstallStatus.NotConfigured).ToList();
        var missing = clients.Where(c => c.Status == McpInstallStatus.NotAvailable).ToList();

        if (configured.Count == 0 && pending.Count == 0)
            return "[dim]No supported MCP clients detected[/]";

        var parts = new List<string>();
        foreach (var (name, _) in configured) parts.Add($"[green]{name} ✓[/]");
        foreach (var (name, _) in pending) parts.Add($"[yellow]{name} ✗ (run `eidet mcp install {name}`)[/]");
        if (missing.Count > 0)
            parts.Add($"[dim]not installed: {string.Join(", ", missing.Select(c => c.Name))}[/]");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Suggest the right way to start the service based on what's installed.
    /// </summary>
    private static async Task<string> GetRestartHintAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (await UpdateCommand.IsScheduledTaskRegisteredAsync(ct))
                    return "schtasks /run /tn \"Eidet\"";
            }
            else if (OperatingSystem.IsMacOS())
            {
                var plistPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "LaunchAgents", "dev.eidet.service.plist");
                if (File.Exists(plistPath))
                    return "launchctl start dev.eidet.service";
            }
            else
            {
                var unitPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "systemd", "user", "eidet.service");
                if (File.Exists(unitPath))
                    return "systemctl --user start eidet.service";
            }
        }
        catch { }

        return "eidet serve";
    }
}
