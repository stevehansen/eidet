using Eidet.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
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

        [CommandOption("--service")]
        public bool RunAsService { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (settings.RunAsService)
            return await RunAsWindowsServiceAsync(cancellation);

        return await RunAsConsoleAsync(settings, cancellation);
    }

    private static async Task<int> RunAsWindowsServiceAsync(CancellationToken cancellation)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHostedService<EidetWindowsService>();
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "Eidet";
        });

        // Configure logging for Windows Event Log
        builder.Logging.ClearProviders();
        if (OperatingSystem.IsWindows())
        {
            builder.Logging.AddEventLog(new EventLogSettings
            {
                SourceName = "Eidet",
                LogName = "Application",
            });
        }

        var host = builder.Build();
        await host.RunAsync(cancellation);
        return 0;
    }

    private static async Task<int> RunAsConsoleAsync(Settings settings, CancellationToken cancellation)
    {
        EidetHost host;
        try
        {
            host = EidetHost.Create(settings.BindAddress, settings.Port);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  RavenDB: [red]Failed[/] — {ex.Message}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold]Eidet[/] v{Eidet.Core.EidetVersion.Current}");

        if (host.StorageMode == StorageMode.Embedded)
            AnsiConsole.MarkupLine($"  RavenDB: [green]Embedded[/] ({Markup.Escape(host.RavenUrl)})");
        else
            AnsiConsole.MarkupLine($"  RavenDB: [green]Connected[/] ({Markup.Escape(host.RavenUrl)})");

        if (host.OllamaEnabled)
        {
            var healthy = await host.CheckOllamaAsync(cancellation);
            AnsiConsole.MarkupLine($"  Ollama:  {(healthy ? "[green]Connected[/]" : "[yellow]Unavailable[/]")} ({host.OllamaModel} @ {Markup.Escape(host.OllamaUrl)})");
        }

        if (host.AuthEnabled)
            AnsiConsole.MarkupLine($"  Auth:    [green]Enabled[/] ({host.ApiKeyCount} key(s))");
        else if (!host.CheckAuthGuard())
        {
            AnsiConsole.MarkupLine("  Auth:    [red]DISABLED — binding to non-localhost without auth![/]");
            AnsiConsole.MarkupLine("           [yellow]Create an API key: eidet api-key create \"my-key\"[/]");
            AnsiConsole.MarkupLine("           [yellow]Or disable guard:  eidet config set auth.requireForNonLocalhost false[/]");
            return 1;
        }
        else
            AnsiConsole.MarkupLine($"  Auth:    [dim]Disabled[/] (localhost only)");

        if (host.HookCount > 0)
            AnsiConsole.MarkupLine($"  Hooks:   [yellow]{host.HookCount} configured[/]");

        AnsiConsole.MarkupLine($"  API:     [green]http://{host.BindAddress}:{host.Port}[/]");
        AnsiConsole.MarkupLine($"  MCP:     http://{host.BindAddress}:{host.Port}/mcp");
        AnsiConsole.MarkupLine($"  Health:  http://{host.BindAddress}:{host.Port}/api/health");
        AnsiConsole.WriteLine();

        host.StartScheduler();
        AnsiConsole.MarkupLine($"  Scheduler: [green]Active[/] (maintenance every {host.MaintenanceIntervalHours}h, consolidation every {host.ConsolidationIntervalHours}h)");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            AnsiConsole.MarkupLine("\n[yellow]Shutting down...[/]");
            cts.Cancel();
        };

        try
        {
            await host.RunAsync(cts.Token);
        }
        finally
        {
            host.Dispose();
            AnsiConsole.MarkupLine("[dim]Eidet stopped.[/]");
        }

        return 0;
    }
}
