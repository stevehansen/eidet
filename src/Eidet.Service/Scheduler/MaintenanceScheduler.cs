using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Service.Scheduler;

/// <summary>
/// Background scheduler that runs maintenance, consolidation, and enrichment
/// at configured intervals. Runs as part of the Eidet service.
/// </summary>
public sealed class MaintenanceScheduler : IDisposable
{
    private readonly IEidetStore _store;
    private readonly MaintenanceService _maintenance;
    private readonly ConsolidationService _consolidation;
    private readonly MemoryService _memorySvc;
    private readonly MaintenanceConfig _config;
    private readonly Timer _maintenanceTimer;
    private readonly Timer _consolidationTimer;
    private bool _running;

    public MaintenanceScheduler(
        IEidetStore store,
        MemoryService memorySvc,
        MaintenanceService maintenance,
        ConsolidationService consolidation,
        MaintenanceConfig config)
    {
        _store = store;
        _memorySvc = memorySvc;
        _maintenance = maintenance;
        _consolidation = consolidation;
        _config = config;

        _maintenanceTimer = new Timer(OnMaintenanceTick, null, Timeout.Infinite, Timeout.Infinite);
        _consolidationTimer = new Timer(OnConsolidationTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        var maintenanceInterval = TimeSpan.FromHours(_config.IntervalHours);
        var consolidationInterval = TimeSpan.FromHours(_config.ConsolidationIntervalHours);

        // Initial delay: 5 minutes for maintenance, 2 minutes for consolidation
        _maintenanceTimer.Change(TimeSpan.FromMinutes(5), maintenanceInterval);
        _consolidationTimer.Change(TimeSpan.FromMinutes(2), consolidationInterval);
    }

    public void Stop()
    {
        _running = false;
        _maintenanceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _consolidationTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private async void OnMaintenanceTick(object? state)
    {
        if (!_running) return;

        try
        {
            // Run maintenance for all active repos
            var repoIds = await GetActiveRepoIdsAsync();
            foreach (var repoId in repoIds)
            {
                var isActive = _memorySvc.IsRepoActive(repoId);
                await _maintenance.RunAsync(repoId, isRepoActive: isActive);
            }
        }
        catch
        {
            // Scheduler should never crash
        }
    }

    private async void OnConsolidationTick(object? state)
    {
        if (!_running) return;

        try
        {
            var repoIds = await GetActiveRepoIdsAsync();
            foreach (var repoId in repoIds)
            {
                await _consolidation.ConsolidateAsync(repoId);
            }
        }
        catch
        {
            // Scheduler should never crash
        }
    }

    private async Task<List<string>> GetActiveRepoIdsAsync()
    {
        try
        {
            return await _store.GetDistinctRepoIdsAsync();
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        Stop();
        _maintenanceTimer.Dispose();
        _consolidationTimer.Dispose();
    }
}
