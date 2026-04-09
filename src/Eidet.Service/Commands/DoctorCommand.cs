using Eidet.Core.Configuration;
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
            checks.Add(new CheckResult("RavenDB", true, "Embedded mode (not yet implemented)"));
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

        // Ollama check (optional)
        checks.Add(await CheckOllamaAsync(config));

        // Config file check
        checks.Add(CheckConfigFile());

        if (settings.Json)
            RenderJson(checks);
        else
            RenderTui(checks);

        store?.Dispose();
        return checks.All(c => c.Passed || c.Optional) ? 0 : 1;
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
