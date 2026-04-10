using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Api;
using Eidet.Service.Mcp;
using Eidet.Service.Scheduler;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class ServeCommand : AsyncCommand<ServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port <PORT>")]
        public int? Port { get; set; }

        [CommandOption("--bind <ADDRESS>")]
        public string? BindAddress { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var port = settings.Port ?? config.Service.Port;
        var bind = settings.BindAddress ?? config.Service.BindAddress;

        AnsiConsole.MarkupLine($"[bold]Eidet[/] v{Eidet.Core.EidetVersion.Current}");

        // Initialize RavenDB
        Raven.Client.Documents.IDocumentStore store;
        try
        {
            store = DocumentStoreFactory.Create(config.Storage.RavenUrl, config.Storage.DatabaseName);
            AnsiConsole.MarkupLine($"  RavenDB: [green]Connected[/] at {config.Storage.RavenUrl}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  RavenDB: [red]Failed[/] — {ex.Message}");
            return 1;
        }

        var eidetStore = new RavenEidetStore(store);
        IEnrichmentService enrichment = config.Enrichment.OllamaEnabled
            ? new OllamaEnrichmentService(config.Enrichment.OllamaUrl, config.Enrichment.OllamaModel)
            : NullEnrichmentService.Instance;
        var layerSvc = new LayerService(eidetStore);
        var memorySvc = new MemoryService(eidetStore, layerSvc);
        var intakeSvc = new IntakeService(eidetStore);
        var consolidationSvc = new ConsolidationService(eidetStore, enrichment);
        var maintenanceSvc = new MaintenanceService(eidetStore, consolidationSvc, enrichment);
        var exportSvc = new ExportService(eidetStore);
        var mcpServer = new McpServer(memorySvc, intakeSvc, consolidationSvc, maintenanceSvc, exportSvc,
            Directory.GetCurrentDirectory());
        var apiServer = new EidetApiServer(memorySvc, intakeSvc, consolidationSvc, maintenanceSvc, exportSvc,
            bind, port, layerSvc, mcpServer);

        if (config.Enrichment.OllamaEnabled)
        {
            var ollamaHealthy = await enrichment.CheckHealthAsync(cancellation);
            AnsiConsole.MarkupLine($"  Ollama:  {(ollamaHealthy ? "[green]Connected[/]" : "[yellow]Unavailable[/]")} ({config.Enrichment.OllamaModel})");
        }

        AnsiConsole.MarkupLine($"  API:     [green]http://{bind}:{port}[/]");
        AnsiConsole.MarkupLine($"  MCP:     http://{bind}:{port}/mcp");
        AnsiConsole.MarkupLine($"  Health:  http://{bind}:{port}/api/health");
        AnsiConsole.WriteLine();

        var scheduler = new MaintenanceScheduler(eidetStore, memorySvc, maintenanceSvc, consolidationSvc, config.Maintenance);
        scheduler.Start();
        AnsiConsole.MarkupLine($"  Scheduler: [green]Active[/] (maintenance every {config.Maintenance.IntervalHours}h, consolidation every {config.Maintenance.ConsolidationIntervalHours}h)");

        try
        {
            await apiServer.RunAsync(cancellation);
        }
        finally
        {
            scheduler.Dispose();
            if (enrichment is IDisposable disposableEnrichment)
                disposableEnrichment.Dispose();
            store.Dispose();
        }

        return 0;
    }
}
