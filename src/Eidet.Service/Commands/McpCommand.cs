using Eidet.Core;
using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
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
        EidetLog.InstallCrashHandlers("mcp");
        EidetLog.Info($"[mcp] Starting (PID {Environment.ProcessId}, workdir={settings.Repo ?? settings.WorkDir ?? Directory.GetCurrentDirectory()})");

        try
        {
            var config = ConfigManager.Load();

            var store = DocumentStoreFactory.CreateFromConfig(config);
            DatabaseProvisioner.DeployIndexes(store);
            var eidetStore = new RavenEidetStore(store);
            using var enrichment = EnrichmentService.CreateFromConfig(config.Enrichment);
            IHookRunner hookRunner = config.Hooks.PreStore.Count > 0 || config.Hooks.PostStore.Count > 0
                || config.Hooks.PreRecall.Count > 0 || config.Hooks.PostRecall.Count > 0
                || config.Hooks.PreForget.Count > 0 || config.Hooks.PostForget.Count > 0
                ? new HookRunner(config.Hooks)
                : NullHookRunner.Instance;
            var memorySvc = new MemoryService(eidetStore, hooks: hookRunner);
            var intakeSvc = new IntakeService(eidetStore, memorySvc);
            var consolidationEngine = new ConsolidationEngine(eidetStore, enrichment, memorySvc);
            IMaintenanceRunner maintenanceRunner = new MaintenanceRunner(
                new MaintenanceOrchestrator(eidetStore, memorySvc, enrichment, consolidationEngine,
                    drift: config.Enrichment.DriftReview));

            var exportSvc = new ExportService(eidetStore, memorySvc);
            var layerSvc = new LayerService(eidetStore);
            var usageTracker = new UsageTracker(store);
            var workDir = settings.Repo ?? settings.WorkDir ?? Directory.GetCurrentDirectory();
            var server = new McpServer(memorySvc, intakeSvc, consolidationEngine, maintenanceRunner, workDir,
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

            EidetLog.Info("[mcp] Stopped cleanly");
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            EidetLog.Info("[mcp] Cancelled");
            return 0;
        }
        catch (Exception ex)
        {
            EidetLog.Error("[mcp] Crashed", ex);
            throw;
        }
    }
}
