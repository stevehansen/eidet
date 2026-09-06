using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;
using Eidet.Core.Domain;

namespace Eidet.Service.Commands;

public sealed class IntakeCommand : AsyncCommand<IntakeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[PATH]")]
        public string? Path { get; set; }

        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store);
        var intakeSvc = new IntakeService(eidetStore, new MemoryService(eidetStore));

        var projectPath = settings.Path ?? Directory.GetCurrentDirectory();
        var repoId = RepoPathResolver.Resolve(projectPath);

        try
        {
            var result = await intakeSvc.IngestAsync(repoId, projectPath, settings.DryRun, cancellation);

            if (settings.Json)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    newCount = result.NewCount,
                    skippedCount = result.SkippedCount,
                    dependencies = result.DetectedLinks.Count,
                    packages = result.ProducedPackages,
                });
                Console.WriteLine(json);
            }
            else
            {
                var mode = settings.DryRun ? "Would ingest" : "Ingested";
                AnsiConsole.MarkupLine($"[bold]{mode}:[/] {result.NewCount} new, {result.SkippedCount} skipped");

                if (result.DetectedLinks.Count > 0)
                    AnsiConsole.MarkupLine($"  Dependencies: {result.DetectedLinks.Count}");
                if (result.ProducedPackages.Count > 0)
                    AnsiConsole.MarkupLine($"  Produces: {string.Join(", ", result.ProducedPackages)}");

                foreach (var item in result.Items.Take(20))
                {
                    var status = item.WasSkipped ? $"[yellow]SKIP ({Markup.Escape(item.SkipReason ?? "")})[/]" : "[green]NEW[/]";
                    AnsiConsole.MarkupLine($"  {status} {Markup.Escape(item.Source)} → {item.Type}: {Markup.Escape(Core.StringUtils.Truncate(item.Content, 60))}");
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
