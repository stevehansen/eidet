using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
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
                    ollamaEnabled = config.Enrichment.OllamaEnabled,
                    ollamaUrl = config.Enrichment.OllamaUrl,
                },
                versionHistory = versionHistory.TakeLast(5).Select(e => new
                {
                    e.Version,
                    e.InstalledAt,
                    e.PreviousVersion,
                    e.Source,
                }),
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
                AnsiConsole.MarkupLine($"  Service:    [dim]Not running[/] — start with [dim]eidet serve[/]");

            AnsiConsole.MarkupLine($"  Storage:    {config.Storage.Mode} RavenDB at [link]{config.Storage.RavenUrl}[/]");
            AnsiConsole.MarkupLine($"  Database:   {config.Storage.DatabaseName}" +
                (docCount.HasValue ? $" ({docCount} documents)" : " [dim](unreachable)[/]"));

            if (ravenVersion != null)
                AnsiConsole.MarkupLine($"  RavenDB:    v{ravenVersion}");

            AnsiConsole.MarkupLine($"  Ollama:     {(config.Enrichment.OllamaEnabled ? config.Enrichment.OllamaUrl : "[dim]Disabled[/]")}");
            AnsiConsole.MarkupLine($"  Config:     {ConfigManager.GetConfigPath()}");

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
}
