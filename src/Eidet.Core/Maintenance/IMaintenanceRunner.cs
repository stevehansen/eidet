namespace Eidet.Core.Maintenance;

/// <summary>
/// Thin default-path facade used by scheduler / REST / MCP. Keeps those callers
/// immune to stage additions: adding a new stage in the pipeline doesn't change
/// this signature.
/// </summary>
public interface IMaintenanceRunner
{
    Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken ct = default);
}

public sealed class MaintenanceRunner : IMaintenanceRunner
{
    private readonly IMaintenanceOrchestrator _orchestrator;

    public MaintenanceRunner(IMaintenanceOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken ct = default)
        => _orchestrator.RunAsync(request, ct);
}
