using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Raven.Client.Documents;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class DoctorCommand : AsyncCommand<DoctorCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var checks = new List<CheckResult>();

        IDocumentStore? store = null;

        // RavenDB connection check
        if (config.Storage.Mode == StorageMode.Embedded)
        {
            try
            {
                var dataDir = config.Storage.DataDir ?? DocumentStoreFactory.GetDefaultDataDir();
                store = DocumentStoreFactory.CreateEmbedded(dataDir, config.Storage.DatabaseName);
                checks.Add(new CheckResult("RavenDB", true, $"Embedded at {dataDir}"));
            }
            catch (Exception ex)
            {
                checks.Add(new CheckResult("RavenDB", false,
                    $"Embedded startup failed: {ex.Message}",
                    Fix: "Check disk space and permissions, or switch to external:\n  eidet config set storage.mode external"));
            }
        }
        else
        {
            try
            {
                store = DocumentStoreFactory.Create(config.Storage.RavenUrl, config.Storage.DatabaseName);
                var buildInfo = await store.Maintenance.Server.SendAsync(
                    new Raven.Client.ServerWide.Operations.GetBuildNumberOperation());
                checks.Add(new CheckResult("RavenDB", true, $"Connected (v{buildInfo.FullVersion}) at {config.Storage.RavenUrl}"));
            }
            catch (Exception ex)
            {
                checks.Add(new CheckResult("RavenDB", false,
                    $"Connection failed: {ex.Message}",
                    Fix: "Start RavenDB or switch to embedded:\n  eidet config set storage.mode embedded"));
            }
        }

        // Database + Index checks (only if connected)
        if (store is not null && checks[0].Passed)
        {
            var ravenStore = new RavenEidetStore(store);
            var info = await ravenStore.GetDatabaseInfoAsync();

            if (info == null)
            {
                checks.Add(new CheckResult("Database", false,
                    $"\"{config.Storage.DatabaseName}\" not found",
                    Fix: "Create it with: eidet setup"));
            }
            else
            {
                checks.Add(new CheckResult("Database", true,
                    $"\"{info.Name}\" ({info.DocumentCount} documents)"));

                checks.Add(info.IndexExists
                    ? new CheckResult("Index", true, "Memories/Search deployed")
                    : new CheckResult("Index", false, "Memories/Search not found",
                        Fix: "Deploy indexes with: eidet setup"));
            }
        }

        // Service check — is the API server running and healthy?
        checks.Add(await CheckServiceAsync());

        // Ollama check (optional)
        checks.Add(await CheckEnrichmentAsync(config));

        // Config file check
        checks.Add(CheckConfigFile());

        if (settings.Json)
            RenderJson(checks);
        else
            RenderTui(checks);

        store?.Dispose();
        return checks.All(c => c.Passed || c.Optional) ? 0 : 1;
    }

    private static async Task<CheckResult> CheckServiceAsync()
    {
        var (running, healthy, info) = await ServiceLock.CheckHealthAsync();

        if (!running)
            return new CheckResult("Service", false,
                "Not running",
                Optional: true,
                Fix: "Start the service: eidet serve\n  Or install as background service: eidet install");

        if (!healthy)
            return new CheckResult("Service", false,
                $"Running (PID {info!.Pid}) but not responding on port {info.Port}",
                Fix: $"The service may be starting up, or port {info.Port} may be blocked.\n  Check: curl http://{info.BindAddress}:{info.Port}/api/health");

        var uptime = DateTimeOffset.UtcNow - info!.StartedAt;
        return new CheckResult("Service", true,
            $"Healthy (PID {info.Pid}, port {info.Port}, up {uptime.TotalHours:F0}h {uptime.Minutes}m)");
    }

    private static async Task<CheckResult> CheckEnrichmentAsync(EidetConfig config)
    {
        var label = config.Enrichment.Provider == EnrichmentProvider.OpenAiCompatible
            ? "Enrichment (OpenAI)" : "Enrichment (Ollama)";

        if (!config.Enrichment.Enabled)
            return new CheckResult(label, true, "Disabled (optional)", Optional: true);

        var openAi = config.Enrichment.Provider == EnrichmentProvider.OpenAiCompatible;
        var probePath = openAi ? "/v1/models" : "/api/tags";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"{config.Enrichment.Url.TrimEnd('/')}{probePath}");
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                var hasModel = body.Contains(config.Enrichment.Model, StringComparison.OrdinalIgnoreCase);
                return hasModel
                    ? new CheckResult(label, true, $"Connected ({config.Enrichment.Model})")
                    : new CheckResult(label, false,
                        $"Connected but model \"{config.Enrichment.Model}\" not found",
                        Optional: true,
                        Fix: openAi
                            ? $"Load \"{config.Enrichment.Model}\" in your server, or: eidet config set enrichment.model <name>"
                            : $"Pull the model: ollama pull {config.Enrichment.Model}");
            }

            return new CheckResult(label, false,
                $"HTTP {(int)response.StatusCode} from {config.Enrichment.Url}",
                Optional: true);
        }
        catch (Exception ex)
        {
            return new CheckResult(label, false,
                $"Connection failed: {ex.Message}",
                Optional: true,
                Fix: "Start the enrichment backend or disable:\n  eidet config set enrichment.enabled false");
        }
    }

    private static CheckResult CheckConfigFile()
    {
        var path = ConfigManager.GetConfigPath();
        if (File.Exists(path))
            return new CheckResult("Config", true, path);

        return new CheckResult("Config", true, "Using defaults (no config file)", Optional: true);
    }

    private static void RenderTui(List<CheckResult> checks)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Eidet Health Check[/]")
            .AddColumn("Component")
            .AddColumn("Status")
            .AddColumn("Details");

        foreach (var check in checks)
        {
            var icon = check.Passed
                ? "[green]✓[/]"
                : check.Optional ? "[yellow]~[/]" : "[red]✗[/]";

            var status = check.Passed ? "[green]OK[/]" :
                check.Optional ? "[yellow]Warn[/]" : "[red]Fail[/]";

            table.AddRow(
                $"  {check.Name}",
                $"{icon} {status}",
                check.Details);

            if (check.Fix != null)
                table.AddRow("", "", $"[dim]Fix: {check.Fix.EscapeMarkup()}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var allPassed = checks.All(c => c.Passed || c.Optional);
        if (allPassed)
            AnsiConsole.MarkupLine("[green]All checks passed[/]");
        else
            AnsiConsole.MarkupLine("[red]Some checks failed — see fixes above[/]");
    }

    private static void RenderJson(List<CheckResult> checks)
    {
        var results = checks.Select(c => new { c.Name, c.Passed, c.Optional, c.Details, c.Fix });
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            healthy = checks.All(c => c.Passed || c.Optional),
            checks = results,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
    }

    private record CheckResult(
        string Name, bool Passed, string Details,
        bool Optional = false, string? Fix = null);
}
