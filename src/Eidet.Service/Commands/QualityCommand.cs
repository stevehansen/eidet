using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Integrity;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class QualityCommand : AsyncCommand<QualityCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-r|--repo <REPO>")]
        public string? Repo { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store);
        var memory = new MemoryService(eidetStore, new LayerService(eidetStore));
        var svc = new QualityService(eidetStore, new IntegrityAuditor(memory, eidetStore));

        var repoId = settings.Repo ?? Directory.GetCurrentDirectory();

        try
        {
            var report = await svc.AnalyzeAsync(repoId, cancellation);

            if (settings.Json)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                });
                Console.WriteLine(json);
                return 0;
            }

            // Score header
            var scoreColor = report.OverallScore >= 0.8f ? "green" : report.OverallScore >= 0.5f ? "yellow" : "red";
            AnsiConsole.MarkupLine($"[bold]Memory Quality:[/] [{scoreColor}]{report.OverallScore:P0}[/] ({report.AnalyzedCount} of {report.TotalMemories} analyzed)");
            AnsiConsole.WriteLine();

            if (report.Issues.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]No issues found.[/]");
            }
            else
            {
                var table = new Table().Border(TableBorder.Simple)
                    .AddColumn("Severity")
                    .AddColumn("Issue")
                    .AddColumn("Count")
                    .AddColumn("Description");

                foreach (var issue in report.Issues)
                {
                    var icon = issue.Severity switch
                    {
                        QualitySeverity.Critical => "[red]CRITICAL[/]",
                        QualitySeverity.Warning => "[yellow]WARNING[/]",
                        _ => "[blue]INFO[/]",
                    };
                    table.AddRow(icon, Markup.Escape(issue.Title),
                        issue.AffectedCount.ToString(), Markup.Escape(issue.Description));
                }

                AnsiConsole.Write(table);
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Breakdown:[/]");
            foreach (var (type, count) in report.Breakdown.TypeDistribution)
                AnsiConsole.MarkupLine($"  {type}: {count}");

            if (report.Breakdown.TopTags.Count > 0)
            {
                AnsiConsole.MarkupLine($"  Top tags: {string.Join(", ", report.Breakdown.TopTags.Select(kv => $"{kv.Key} ({kv.Value})"))}");
            }

            AnsiConsole.MarkupLine($"  Avg importance: {report.Breakdown.AverageImportance:F2}  Avg confidence: {report.Breakdown.AverageConfidence:F2}");

            if (report.Breakdown.Reflection is { } rh)
            {
                var rateColor = rh.EchoRate >= 0.5f ? "green" : rh.EchoRate >= 0.25f ? "yellow" : "red";
                AnsiConsole.MarkupLine(
                    $"  Reflected memories: {rh.Total} — echo rate [{rateColor}]{rh.EchoRate:P0}[/] " +
                    $"({rh.Echoed} echoed, {rh.NetNegative} net-negative, {rh.Untouched} untouched)");
            }
        }
        finally
        {
            store.Dispose();
        }

        return 0;
    }
}
