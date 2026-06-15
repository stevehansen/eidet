using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;
using Eidet.Core.LooseEnds;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Api;

/// <summary>
/// Bundle of services and configuration consumed by <see cref="EidetApiServer"/>.
/// Required collaborators are marked <c>required</c>; everything else is opt-in
/// and degrades gracefully when omitted (e.g. no MCP-over-HTTP, no quality
/// reports, no auth gate).
/// </summary>
public sealed record EidetApiServerOptions
{
    public required MemoryService Memory { get; init; }
    public required IntakeService Intake { get; init; }
    public required ConsolidationEngine Consolidation { get; init; }
    public required IMaintenanceRunner Maintenance { get; init; }
    public required ExportService Export { get; init; }
    public required LooseEndService LooseEnds { get; init; }
    public required string BindAddress { get; init; }
    public required int Port { get; init; }

    public LayerService? Layers { get; init; }
    public LayerSyncService? LayerSync { get; init; }
    public McpServer? Mcp { get; init; }
    public AuthConfig? Auth { get; init; }
    public QualityService? Quality { get; init; }
    public EnrichmentService? Enrichment { get; init; }
    public EidetConfig? Config { get; init; }
    public UsageTracker? Usage { get; init; }
    public ScheduledTaskService? ScheduledTasks { get; init; }
}
