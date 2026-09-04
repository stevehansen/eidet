using Eidet.Core;
using Eidet.Core.Configuration;
using Eidet.Core.Services;
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
        EidetLog.InstallCrashHandlers("serve");

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
        var config = ConfigManager.Load();
        var actualPort = settings.Port ?? config.Service.Port;
        var actualBind = settings.BindAddress ?? config.Service.BindAddress;

        // Acquire service lock — prevents double-serve
        var serviceLock = new ServiceLock();
        var priorLock = ServiceLock.Read();
        if (!serviceLock.TryAcquire(actualPort, actualBind, out var existing))
        {
            if (existing != null)
            {
                AnsiConsole.MarkupLine($"[red]Eidet is already running[/] (PID {existing.Pid} on {existing.BindAddress}:{existing.Port})");
                AnsiConsole.MarkupLine($"  Started: {existing.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm}");
                AnsiConsole.MarkupLine($"  [dim]Stop the existing instance first, or use a different port:[/]");
                AnsiConsole.MarkupLine($"  [dim]  eidet serve --port {existing.Port + 1}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Could not acquire service lock.[/]");
            }
            return 1;
        }

        EidetHost host;
        try
        {
            host = EidetHost.Create(settings.BindAddress, settings.Port);
        }
        catch (Exception ex)
        {
            EidetLog.Error("Failed to start Eidet host", ex);
            serviceLock.Dispose();
            AnsiConsole.MarkupLine($"  RavenDB: [red]Failed[/] — {ex.Message}");
            return 1;
        }

        if (priorLock != null && priorLock.Pid != Environment.ProcessId)
            EidetLog.Warn($"Stale lock recovered — prior PID {priorLock.Pid} started {priorLock.StartedAt:O} exited without cleanup");

        EidetLog.Info($"Eidet v{Eidet.Core.EidetVersion.Current} starting on {host.BindAddress}:{host.Port} (PID {Environment.ProcessId})");
        AnsiConsole.MarkupLine($"[bold]Eidet[/] v{Eidet.Core.EidetVersion.Current}");

        // Hint when run manually (no autostart + no MCP client integration set up).
        if (!await UpdateCommand.IsServiceRegisteredAsync(cancellation))
        {
            AnsiConsole.MarkupLine("  [yellow]Tip:[/] running manually. " +
                "Run [dim]eidet install[/] to autostart at login and register with Claude Code / Desktop / Codex / Gemini.");
        }

        if (host.StorageMode == StorageMode.Embedded)
            AnsiConsole.MarkupLine($"  RavenDB: [green]Embedded[/] ({Markup.Escape(host.RavenUrl)})");
        else
            AnsiConsole.MarkupLine($"  RavenDB: [green]Connected[/] ({Markup.Escape(host.RavenUrl)})");

        if (host.EnrichmentEnabled)
        {
            var healthy = await host.CheckEnrichmentAsync(cancellation);
            var fallbacks = host.EnrichmentFallbackCount switch { 0 => "", 1 => ", +1 fallback", var n => $", +{n} fallbacks" };
            AnsiConsole.MarkupLine($"  Enrichment: {(healthy ? "[green]Connected[/]" : "[yellow]Unavailable[/]")} ({host.EnrichmentModel} @ {Markup.Escape(host.EnrichmentUrl)}{fallbacks})");
        }

        if (host.AuthEnabled)
            AnsiConsole.MarkupLine($"  Auth:    [green]Enabled[/] ({host.ApiKeyCount} key(s))");
        else if (!host.CheckAuthGuard())
        {
            AnsiConsole.MarkupLine("  Auth:    [red]DISABLED — binding to non-localhost without auth![/]");
            AnsiConsole.MarkupLine("           [yellow]Create an API key: eidet api-key create \"my-key\"[/]");
            AnsiConsole.MarkupLine("           [yellow]Or disable guard:  eidet config set auth.requireForNonLocalhost false[/]");
            serviceLock.Dispose();
            return 1;
        }
        else
            AnsiConsole.MarkupLine($"  Auth:    [dim]Disabled[/] (localhost only)");

        if (host.HooksEnabled)
            AnsiConsole.MarkupLine("  Hooks:   [yellow]configured[/]");

        AnsiConsole.MarkupLine($"  API:     [green]http://{host.BindAddress}:{host.Port}[/]");
        AnsiConsole.MarkupLine($"  MCP:     http://{host.BindAddress}:{host.Port}/mcp");
        AnsiConsole.MarkupLine($"  Health:  http://{host.BindAddress}:{host.Port}/api/health");
        AnsiConsole.WriteLine();

        await host.StartSchedulerAsync(cancellation);
        AnsiConsole.MarkupLine($"  Scheduler: [green]Active[/] (persisted — maintenance every {host.MaintenanceIntervalHours}h at {host.MaintenanceAtLocalTime} local, consolidation every {host.ConsolidationIntervalHours}h)");
        AnsiConsole.MarkupLine(host.NightlyModelWorkEnabled
            ? $"  Nightly AI: [green]Active[/] ({Markup.Escape(host.NightlyModelWork)})"
            : $"  Nightly AI: [dim]Off[/] ({Markup.Escape(host.NightlyModelWork)})");

        if (host.EnrichmentEnabled)
        {
            await host.StartEnrichmentWorkerAsync(cancellation);
            AnsiConsole.MarkupLine("  Enrichment: [green]Active[/] (RavenDB subscription — enriches on store)");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        // Start background health monitor — prints timestamped updates when dependency status changes
        var healthMonitor = host.StartHealthMonitor(cts.Token);
        healthMonitor.OnStatusChanged += (component, healthy, detail) =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var status = healthy ? $"[green]{Markup.Escape(detail)}[/]" : $"[yellow]{Markup.Escape(detail)}[/]";
            AnsiConsole.MarkupLine($"  [dim]{timestamp}[/] {component}: {status}");
        };
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
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Graceful shutdown via Ctrl+C — exit 0
        }
        catch (System.Net.HttpListenerException ex) when (ex.ErrorCode == 183 || ex.ErrorCode == 48)
        {
            // ERROR_ALREADY_EXISTS (Windows) or EADDRINUSE (Unix) — port is in use
            AnsiConsole.MarkupLine($"\n[red]Port {host.Port} is already in use.[/]");
            AnsiConsole.MarkupLine($"  [dim]Another process is listening on that port.[/]");
            AnsiConsole.MarkupLine($"  [dim]Try a different port:  eidet serve --port {host.Port + 1}[/]");
            AnsiConsole.MarkupLine($"  [dim]Or change the default: eidet config set service.port {host.Port + 1}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            // Unexpected crash — exit non-zero so Task Scheduler triggers RestartOnFailure
            EidetLog.Error("Eidet crashed", ex);
            AnsiConsole.MarkupLine($"\n[red]Eidet crashed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            EidetLog.Info("Eidet stopped");
            serviceLock.Dispose();
            host.Dispose();
            AnsiConsole.MarkupLine("[dim]Eidet stopped.[/]");
        }

        return 0;
    }
}
