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

        /// <summary>
        /// Comma-separated <see cref="MaintenanceStep"/> names to run, skipping every other stage.
        /// Without it a run fires the enrichment, drift-review and reflection stages, which call an
        /// LLM backend — too heavy when the intent is one targeted stage over many repos.
        /// </summary>
        [CommandOption("--only <STAGES>")]
        public string? Only { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store, config);
        using var enrichment = EnrichmentService.CreateFromConfig(config.Enrichment);
        var memorySvc = new MemoryService(eidetStore);
        var consolidationEngine = new ConsolidationEngine(eidetStore, enrichment, memorySvc);
        var reflectionEngine = new ReflectionEngine(
            eidetStore, enrichment, memorySvc, new RavenLooseEndStore(store), config.Enrichment.Reflection);
        IMaintenanceRunner runner = new MaintenanceOrchestrator(
            eidetStore, memorySvc, enrichment, consolidationEngine,
            drift: config.Enrichment.DriftReview,
            reflection: reflectionEngine);

        var repoId = Eidet.Core.Domain.RepoIdNormalizer.Normalize(settings.Repo ?? Directory.GetCurrentDirectory());

        ISet<MaintenanceStep>? only = null;
        if (!string.IsNullOrWhiteSpace(settings.Only))
        {
            only = new HashSet<MaintenanceStep>();
            foreach (var name in settings.Only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<MaintenanceStep>(name, ignoreCase: true, out var step))
                {
                    AnsiConsole.MarkupLine($"[red]Unknown stage '{Markup.Escape(name)}'.[/] Valid: {string.Join(", ", Enum.GetNames<MaintenanceStep>())}");
                    store.Dispose();
                    return 1;
                }
                only.Add(step);
            }
        }

        try
        {
            var report = await runner.RunAsync(
                new MaintenanceRequest { RepoId = repoId, OnlyStages = only }, cancellation);

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
