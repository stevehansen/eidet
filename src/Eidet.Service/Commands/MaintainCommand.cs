using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class MaintainCommand : AsyncCommand<MaintainCommand.Settings>
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
        using var enrichment = EnrichmentService.CreateFromConfig(config.Enrichment);
        var memorySvc = new MemoryService(eidetStore);
        var consolidationEngine = new ConsolidationEngine(eidetStore, enrichment, memorySvc);
        IMaintenanceRunner runner = new MaintenanceRunner(
            new MaintenanceOrchestrator(eidetStore, memorySvc, enrichment, consolidationEngine));

        var repoId = Eidet.Core.Domain.RepoIdNormalizer.Normalize(settings.Repo ?? Directory.GetCurrentDirectory());

        try
        {
            var report = await runner.RunAsync(new MaintenanceRequest { RepoId = repoId }, cancellation);

            if (settings.Json)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(report);
                Console.WriteLine(json);
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold]Maintenance complete[/] — {Markup.Escape(repoId)}");
                foreach (var stage in report.Stages)
                {
                    var label = stage.Succeeded
                        ? $"{stage.Affected}"
                        : $"[red]ERROR[/] {Markup.Escape(stage.Error!)}";
                    AnsiConsole.MarkupLine($"  {stage.Name,-32} {label}");
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
