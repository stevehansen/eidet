using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tools;

/// <summary>
/// Builds the standard 13-handler <see cref="ToolDispatcher"/> shared by REST
/// (<c>EidetApiServer</c>) and MCP (<c>McpServer</c>). Centralising the handler
/// list keeps the two front-ends in lock-step — adding a new tool means editing
/// one file, not two.
/// </summary>
internal static class ToolDispatcherFactory
{
    public static ToolDispatcher Create(
        MemoryService svc,
        IntakeService intake,
        ConsolidationEngine consolidation,
        IMaintenanceRunner maintenance,
        ExportService? export = null,
        LayerService? layers = null,
        UsageTracker? usage = null) =>
        new([
            new StoreToolHandler(svc),
            new RecallToolHandler(svc, layers),
            new ForgetToolHandler(svc),
            new FeedbackToolHandler(svc),
            new HistoryToolHandler(svc),
            new ContextToolHandler(svc),
            new LinkToolHandler(svc),
            new ConsolidateToolHandler(consolidation),
            new MaintenanceToolHandler(maintenance, svc),
            new EditToolHandler(svc),
            new IntakeToolHandler(intake),
            new PackExportToolHandler(export),
            new PackImportToolHandler(export, layers),
        ], usage);
}
