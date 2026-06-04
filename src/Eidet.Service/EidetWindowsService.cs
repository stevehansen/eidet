using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eidet.Service;

/// <summary>
/// BackgroundService that hosts Eidet as a Windows Service.
/// Used when 'eidet serve --service' is passed.
/// </summary>
public sealed class EidetWindowsService : BackgroundService
{
    private readonly ILogger<EidetWindowsService> _logger;
    private EidetHost? _host;

    public EidetWindowsService(ILogger<EidetWindowsService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _host = EidetHost.Create();
            _logger.LogInformation("Eidet v{Version} starting on http://{Bind}:{Port}",
                Eidet.Core.EidetVersion.Current, _host.BindAddress, _host.Port);

            if (_host.EnrichmentEnabled)
            {
                var healthy = await _host.CheckEnrichmentAsync(stoppingToken);
                _logger.LogInformation("Enrichment: {Status} ({Model})",
                    healthy ? "Connected" : "Unavailable", _host.EnrichmentModel);
            }

            if (!_host.CheckAuthGuard())
            {
                _logger.LogError("Non-localhost binding without auth enabled — refusing to start");
                return;
            }

            await _host.StartSchedulerAsync(stoppingToken);
            _logger.LogInformation("Scheduler active (persisted — maintenance every {M}h, consolidation every {C}h)",
                _host.MaintenanceIntervalHours, _host.ConsolidationIntervalHours);

            if (_host.EnrichmentEnabled)
            {
                await _host.StartEnrichmentWorkerAsync(stoppingToken);
                _logger.LogInformation("Enrichment worker active (RavenDB subscription)");
            }

            await _host.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Eidet stopping...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Eidet failed");
            throw;
        }
        finally
        {
            _host?.Dispose();
            _logger.LogInformation("Eidet stopped.");
        }
    }
}
