using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class RecallCommand : AsyncCommand<RecallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<QUERY>")]
        public string Query { get; set; } = "";

        [CommandOption("-r|--repo <REPO>")]
        public string? Repo { get; set; }

        [CommandOption("-t|--type <TYPE>")]
        public string? Type { get; set; }

        [CommandOption("-l|--limit <LIMIT>")]
        public int? Limit { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.Create(config.Storage.RavenUrl, config.Storage.DatabaseName);
        var eidetStore = new RavenEidetStore(store);
        var svc = new MemoryService(eidetStore);

        var repoId = settings.Repo ?? Directory.GetCurrentDirectory();

        var query = new MemoryQuery
        {
            Text = settings.Query,
            Limit = settings.Limit ?? 10,
            Type = !string.IsNullOrEmpty(settings.Type) && Enum.TryParse<MemoryType>(settings.Type, true, out var t) ? t : null,
        };

        try
        {
            var results = await svc.RecallAsync(repoId, query, cancellation);

            if (settings.Json)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                });
                Console.WriteLine(json);
            }
            else
            {
                if (results.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No memories found.[/]");
                    return 0;
                }

                AnsiConsole.MarkupLine($"[bold]{results.Count}[/] result(s):");
                foreach (var r in results)
                {
                    var prefix = r.Type switch
                    {
                        MemoryType.Insight => "[blue][[I]][/]",
                        MemoryType.Observation => "[grey][[O]][/]",
                        MemoryType.Procedure => "[green][[P]][/]",
                        MemoryType.Heuristic => "[yellow][[H]][/]",
                        _ => "[[?]]",
                    };
                    var display = Markup.Escape(r.OneLiner ?? Core.StringUtils.Truncate(r.Content, 100));
                    var stale = r.StalenessWarning != null ? $" [dim]{Markup.Escape(r.StalenessWarning)}[/]" : "";
                    AnsiConsole.MarkupLine($"  {prefix} {display}{stale}");
                    AnsiConsole.MarkupLine($"      [dim]id={Markup.Escape(r.Id)} importance={r.Importance:F2} score={r.Score:F2}[/]");
                }
            }
        }
        finally
        {
            store.Dispose();
        }

        return 0;
    }
}
