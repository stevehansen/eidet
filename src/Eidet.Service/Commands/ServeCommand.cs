using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Api;
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
        var memorySvc = new MemoryService(eidetStore);
        var intakeSvc = new IntakeService(eidetStore);
        var consolidationSvc = new ConsolidationService(eidetStore);
        var maintenanceSvc = new MaintenanceService(eidetStore, consolidationSvc);
        var exportSvc = new ExportService(eidetStore);
        var apiServer = new EidetApiServer(memorySvc, intakeSvc, consolidationSvc, maintenanceSvc, exportSvc, bind, port);

        AnsiConsole.MarkupLine($"  API:     [green]http://{bind}:{port}[/]");
        AnsiConsole.MarkupLine($"  Health:  http://{bind}:{port}/api/health");
        AnsiConsole.WriteLine();

        try
        {
            await apiServer.RunAsync(cancellation);
        }
        finally
        {
            store.Dispose();
        }

        return 0;
    }
}
