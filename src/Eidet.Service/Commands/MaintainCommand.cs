using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;
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
        using var enrichment = config.Enrichment.OllamaEnabled
            ? EnrichmentService.CreateOllama(config.Enrichment.OllamaUrl, config.Enrichment.OllamaModel)
            : EnrichmentService.CreateNull();
        var consolidationSvc = new ConsolidationService(eidetStore, enrichment);
        var maintenanceSvc = new MaintenanceService(eidetStore, consolidationSvc, enrichment);

        var repoId = Eidet.Core.Domain.RepoIdNormalizer.Normalize(settings.Repo ?? Directory.GetCurrentDirectory());

        try
        {
            var result = await maintenanceSvc.RunAsync(repoId, ct: cancellation);

            if (settings.Json)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(result);
                Console.WriteLine(json);
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold]Maintenance complete[/] — {Markup.Escape(repoId)}");
                AnsiConsole.MarkupLine($"  TTL expired:    {result.ExpiredByTtl}");
                AnsiConsole.MarkupLine($"  Retention:      {result.ExpiredByRetention}");
                AnsiConsole.MarkupLine($"  Dedup merged:   {result.DedupMerged}");
                AnsiConsole.MarkupLine($"  Decay updated:  {result.DecayUpdated}");
                AnsiConsole.MarkupLine($"  Orphans:        {result.OrphansCleaned}");
                AnsiConsole.MarkupLine($"  Enrichment fix: {result.EnrichmentCleaned}");
                AnsiConsole.MarkupLine($"  Backfill:       {result.BackfillEnriched}");
                AnsiConsole.MarkupLine($"  Ollama:         {result.OllamaEnriched}");
                AnsiConsole.MarkupLine($"  Consolidated:   {result.ConsolidatedInsights}");
            }
        }
        finally
        {
            store.Dispose();
        }

        return 0;
    }
}
