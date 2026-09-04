using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;
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

        // Enrichment backends (optional) — one row per backend in the chain
        checks.AddRange(await CheckEnrichmentAsync(config));

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

    private static async Task<List<CheckResult>> CheckEnrichmentAsync(EidetConfig config)
    {
        var enrichment = config.Enrichment;
        if (!enrichment.Enabled)
            return [new CheckResult(BackendLabel(enrichment, 0), true, "Disabled (optional)", Optional: true)];

        var results = new List<CheckResult>();
        var backends = enrichment.Backends;
        for (var i = 0; i < backends.Count; i++)
            results.Add(await CheckBackendAsync(backends[i], BackendLabel(backends[i], i), primary: i == 0));
        return results;
    }

    private static string BackendLabel(EnrichmentBackendConfig backend, int index)
    {
        var kind = backend.Provider == EnrichmentProvider.OpenAiCompatible ? "OpenAI" : "Ollama";
        return index == 0 ? $"Enrichment ({kind})" : $"Enrichment fallback {index} ({kind})";
    }

    // A fallback's settings live in the enrichment.fallbacks array, which `eidet config set`
    // does not address — so its fixes point at the file instead of at a key.
    private static async Task<CheckResult> CheckBackendAsync(EnrichmentBackendConfig backend, string label, bool primary)
    {
        var openAi = backend.Provider == EnrichmentProvider.OpenAiCompatible;
        var where = primary ? null : $"this entry of enrichment.fallbacks in {ConfigManager.GetConfigPath()}";

        try
        {
            using var http = EnrichmentHttp.CreateClient(backend, TimeSpan.FromSeconds(5));
            var response = await http.GetAsync(EnrichmentHttp.ProbePath(backend.Provider));
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                var hasModel = body.Contains(backend.Model, StringComparison.OrdinalIgnoreCase);
                return hasModel
                    ? new CheckResult(label, true, $"Connected ({backend.Model})")
                    : new CheckResult(label, false,
                        $"Connected but model \"{backend.Model}\" not found",
                        Optional: true,
                        Fix: openAi
                            ? $"Load \"{backend.Model}\" in your server, or set the model: {where ?? "eidet config set enrichment.model <name>"}"
                            : $"Pull the model: ollama pull {backend.Model}");
            }

            var status = (int)response.StatusCode;
            return new CheckResult(label, false,
                $"HTTP {status} from {backend.Url}",
                Optional: true,
                Fix: status is 401 or 403
                    ? $"The server wants a bearer token: {where ?? "eidet config set enrichment.apiKey <KEY>"}"
                    : null);
        }
        catch (Exception ex)
        {
            return new CheckResult(label, false,
                $"Connection failed: {ex.Message}",
                Optional: true,
                Fix: primary
                    ? "Start the enrichment backend or disable:\n  eidet config set enrichment.enabled false"
                    : "Start the fallback backend, or remove it from enrichment.fallbacks");
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
