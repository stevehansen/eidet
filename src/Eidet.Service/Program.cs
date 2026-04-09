using Eidet.Service.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("eidet");
    config.SetApplicationVersion("0.1.0");

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Test connections and troubleshoot issues");

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show service status and stats");
});

return app.Run(args);
