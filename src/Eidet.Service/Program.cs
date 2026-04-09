using Eidet.Core;
using Eidet.Service.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("eidet");
    config.SetApplicationVersion(EidetVersion.Current);

    config.AddCommand<SetupCommand>("setup")
        .WithDescription("First-time configuration wizard");

    config.AddCommand<McpCommand>("mcp")
        .WithDescription("Start MCP server (stdio transport for AI clients)");

    config.AddCommand<ServeCommand>("serve")
        .WithDescription("Start the Eidet REST API service");

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Test connections and troubleshoot issues");

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show service status and stats");
});

return app.Run(args);
