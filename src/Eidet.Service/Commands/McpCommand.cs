using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Mcp;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class McpCommand : AsyncCommand<McpCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--workdir <PATH>")]
        public string? WorkDir { get; set; }

        [CommandOption("--repo <REPO>")]
        public string? Repo { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();

        var store = DocumentStoreFactory.CreateFromConfig(config);
        DatabaseProvisioner.DeployIndexes(store);
        var eidetStore = new RavenEidetStore(store);
        IEnrichmentService enrichment = config.Enrichment.OllamaEnabled
            ? new OllamaEnrichmentService(config.Enrichment.OllamaUrl, config.Enrichment.OllamaModel)
            : NullEnrichmentService.Instance;
        IHookRunner hookRunner = config.Hooks.PreStore.Count > 0 || config.Hooks.PostStore.Count > 0
            || config.Hooks.PreRecall.Count > 0 || config.Hooks.PostRecall.Count > 0
            || config.Hooks.PreForget.Count > 0 || config.Hooks.PostForget.Count > 0
            ? new HookRunner(config.Hooks)
            : NullHookRunner.Instance;
        var memorySvc = new MemoryService(eidetStore, hooks: hookRunner);
        var intakeSvc = new IntakeService(eidetStore);
        var consolidationSvc = new ConsolidationService(eidetStore, enrichment);
        var maintenanceSvc = new MaintenanceService(eidetStore, consolidationSvc, enrichment);

        var exportSvc = new ExportService(eidetStore);
        var layerSvc = new LayerService(eidetStore);
        var usageTracker = new UsageTracker(store);
        var workDir = settings.Repo ?? settings.WorkDir ?? Directory.GetCurrentDirectory();
        var server = new McpServer(memorySvc, intakeSvc, consolidationSvc, maintenanceSvc, workDir,
            autoIntake: config.Memory.AutoIntakeOnFirstSession, usage: usageTracker,
            export: exportSvc, layers: layerSvc);

        try
        {
            await server.RunStdioAsync(cancellation);
        }
        finally
        {
            store.Dispose();
        }

        return 0;
    }
}
