using Eidet.Core.LooseEnds;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tools;

/// <summary>
/// Builds the standard handler set for the <see cref="ToolDispatcher"/> shared by REST
/// (<c>EidetApiServer</c>) and MCP (<c>McpServer</c>) — 15 handlers, plus <c>eidet_reflect</c>
/// when a <see cref="ReflectionEngine"/> is supplied. Centralising the handler list keeps the two
/// front-ends in lock-step — adding a new tool means editing one file, not two.
/// </summary>
internal static class ToolDispatcherFactory
{
    public static ToolDispatcher Create(
        MemoryService svc,
        IntakeService intake,
        ConsolidationEngine consolidation,
        IMaintenanceRunner maintenance,
        LooseEndService looseEnds,
        ExportService? export = null,
        LayerService? layers = null,
        UsageTracker? usage = null,
        ReflectionEngine? reflection = null)
    {
        var handlers = new List<IToolHandler>
        {
            new StoreToolHandler(svc),
            new RecallToolHandler(svc, looseEnds),
            new ForgetToolHandler(svc),
            new FeedbackToolHandler(svc),
            new HistoryToolHandler(svc),
            new ContextToolHandler(svc),
            new LinkToolHandler(svc),
            new ConsolidateToolHandler(consolidation),
            new MaintenanceToolHandler(maintenance),
            new EditToolHandler(svc),
            new IntakeToolHandler(intake),
            new PackExportToolHandler(export),
            new PackImportToolHandler(export, layers),
            new ParkToolHandler(looseEnds),
            new ResolveToolHandler(looseEnds),
        };

        // Reflection (off-MCP, REST/CLI only) is registered only when its engine is wired — the loose-end
        // residue arm needs a store the MCP-stdio path doesn't build. Callers that omit it (MCP, tests)
        // simply don't expose eidet_reflect.
        if (reflection is not null)
            handlers.Add(new ReflectToolHandler(reflection));

        return new ToolDispatcher(handlers, usage);
    }
}
