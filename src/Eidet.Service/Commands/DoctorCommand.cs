using Eidet.Core.Configuration;
using Eidet.Core.Storage;
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

        // RavenDB connection check
        checks.Add(await CheckRavenConnectionAsync(config));

        // Database check
        if (checks[0].Passed)
        {
            checks.Add(await CheckDatabaseAsync(config));

            // Index check (only if database exists)
            if (checks[1].Passed)
                checks.Add(await CheckIndexAsync(config));
        }

        // Ollama check (optional)
        checks.Add(await CheckOllamaAsync(config));

        // Config file check
        checks.Add(CheckConfigFile());

        if (settings.Json)
        {
            RenderJson(checks);
        }
        else
        {
            RenderTui(checks);
        }

        return checks.All(c => c.Passed || c.Optional) ? 0 : 1;
    }

    private static async Task<CheckResult> CheckRavenConnectionAsync(EidetConfig config)
    {
        if (config.Storage.Mode == "embedded")
        {
            return new CheckResult("RavenDB", true, "Embedded mode (not yet implemented)");
        }

        try
        {
            using var store = DocumentStoreFactory.Create(config.Storage.RavenUrl, config.Storage.DatabaseName);

            var buildInfo = await store.Maintenance.Server.SendAsync(
                new Raven.Client.ServerWide.Operations.GetBuildNumberOperation());

            return new CheckResult("RavenDB", true, $"Connected (v{buildInfo.FullVersion}) at {config.Storage.RavenUrl}");
        }
        catch (Exception ex)
        {
            return new CheckResult("RavenDB", false,
                $"Connection failed: {ex.Message}",
                Fix: config.Storage.Mode == "external"
                    ? $"Start RavenDB or switch to embedded:\n  eidet config set storage.mode embedded"
                    : null);
        }
    }

    private static async Task<CheckResult> CheckDatabaseAsync(EidetConfig config)
    {
        try
        {
            using var store = DocumentStoreFactory.Create(config.Storage.RavenUrl, config.Storage.DatabaseName);
            var ravenStore = new RavenEidetStore(store);
            var info = await ravenStore.GetDatabaseInfoAsync();

            if (info == null)
                return new CheckResult("Database", false,
                    $"\"{config.Storage.DatabaseName}\" not found",
                    Fix: "Create it with: eidet setup");

            return new CheckResult("Database", true,
                $"\"{info.Name}\" ({info.DocumentCount} documents)");
        }
        catch (Exception ex)
        {
            return new CheckResult("Database", false, ex.Message);
        }
    }

    private static async Task<CheckResult> CheckIndexAsync(EidetConfig config)
    {
        try
        {
            using var store = DocumentStoreFactory.Create(config.Storage.RavenUrl, config.Storage.DatabaseName);
            var ravenStore = new RavenEidetStore(store);
            var info = await ravenStore.GetDatabaseInfoAsync();

            if (info?.IndexExists == true)
                return new CheckResult("Index", true, "Memories/Search deployed");

            return new CheckResult("Index", false,
                "Memories/Search not found",
                Fix: "Deploy indexes with: eidet setup");
        }
        catch (Exception ex)
        {
            return new CheckResult("Index", false, ex.Message);
        }
    }

    private static async Task<CheckResult> CheckOllamaAsync(EidetConfig config)
    {
        if (!config.Enrichment.OllamaEnabled)
            return new CheckResult("Ollama", true, "Disabled (optional)", Optional: true);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"{config.Enrichment.OllamaUrl}/api/version");
            if (response.IsSuccessStatusCode)
                return new CheckResult("Ollama", true, $"Connected at {config.Enrichment.OllamaUrl}");

            return new CheckResult("Ollama", false,
                $"HTTP {(int)response.StatusCode} from {config.Enrichment.OllamaUrl}",
                Optional: true);
        }
        catch (Exception ex)
        {
            return new CheckResult("Ollama", false,
                $"Connection failed: {ex.Message}",
                Optional: true,
                Fix: "Install Ollama from https://ollama.ai or disable:\n  eidet config set enrichment.ollamaEnabled false");
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
            {
                table.AddRow("", "", $"[dim]Fix: {check.Fix.EscapeMarkup()}[/]");
            }
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
        var results = checks.Select(c => new
        {
            c.Name,
            c.Passed,
            c.Optional,
            c.Details,
            c.Fix,
        });

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            healthy = checks.All(c => c.Passed || c.Optional),
            checks = results,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        Console.WriteLine(json);
    }

    private record CheckResult(
        string Name,
        bool Passed,
        string Details,
        bool Optional = false,
        string? Fix = null);
}
