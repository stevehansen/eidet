namespace Eidet.Core.Maintenance;

/// <summary>
/// Sole entry point for the maintenance pipeline (scheduler / REST / MCP / CLI). Keeps callers
/// immune to stage additions: adding a stage doesn't change these signatures.
/// </summary>
public interface IMaintenanceRunner
{
    /// <summary>
    /// Happy path: normalize the repo path and derive <c>IsRepoActive</c> internally, run all stages.
    /// </summary>
    Task<MaintenanceReport> RunAsync(string repoPathOrId, CancellationToken ct = default);

    /// <summary>Power path: subset/skip/retention overrides and an explicit <c>IsRepoActive</c>.</summary>
    Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken ct = default);
}
