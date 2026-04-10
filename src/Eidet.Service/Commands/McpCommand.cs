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
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();

        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store);
        IEnrichmentService enrichment = config.Enrichment.OllamaEnabled
            ? new OllamaEnrichmentService(config.Enrichment.OllamaUrl, config.Enrichment.OllamaModel)
            : NullEnrichmentService.Instance;
        var memorySvc = new MemoryService(eidetStore);
        var intakeSvc = new IntakeService(eidetStore);
        var consolidationSvc = new ConsolidationService(eidetStore, enrichment);
        var maintenanceSvc = new MaintenanceService(eidetStore, consolidationSvc, enrichment);
        var exportSvc = new ExportService(eidetStore);

        var workDir = settings.WorkDir ?? Directory.GetCurrentDirectory();
        var server = new McpServer(memorySvc, intakeSvc, consolidationSvc, maintenanceSvc, exportSvc, workDir,
            autoIntake: config.Memory.AutoIntakeOnFirstSession);

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
