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

        var store = DocumentStoreFactory.Create(config.Storage.RavenUrl, config.Storage.DatabaseName);
        var eidetStore = new RavenEidetStore(store);
        var memorySvc = new MemoryService(eidetStore);

        var workDir = settings.WorkDir ?? Directory.GetCurrentDirectory();
        var server = new McpServer(memorySvc, workDir);

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
