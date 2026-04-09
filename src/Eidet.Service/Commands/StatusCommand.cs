using Eidet.Core.Configuration;
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
            using var store = DocumentStoreFactory.Create(config.Storage.RavenUrl, config.Storage.DatabaseName);
            var ravenStore = new RavenEidetStore(store);
            var info = await ravenStore.GetDatabaseInfoAsync();

            if (info != null)
            {
                ravenVersion = info.ServerVersion;
                docCount = info.DocumentCount;
            }
        }
        catch { }

        if (settings.Json)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                version = "0.1.0",
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
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            Console.WriteLine(json);
        }
        else
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Eidet[/] v0.1.0");
            AnsiConsole.MarkupLine($"  Storage:    {config.Storage.Mode} RavenDB at [link]{config.Storage.RavenUrl}[/]");
            AnsiConsole.MarkupLine($"  Database:   {config.Storage.DatabaseName}" +
                (docCount.HasValue ? $" ({docCount} documents)" : " [dim](unreachable)[/]"));

            if (ravenVersion != null)
                AnsiConsole.MarkupLine($"  RavenDB:    v{ravenVersion}");

            AnsiConsole.MarkupLine($"  Ollama:     {(config.Enrichment.OllamaEnabled ? config.Enrichment.OllamaUrl : "[dim]Disabled[/]")}");
            AnsiConsole.MarkupLine($"  Config:     {ConfigManager.GetConfigPath()}");
            AnsiConsole.WriteLine();
        }

        return 0;
    }
}
