using System.Net;
using Eidet.Core;
using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Service.Api.Endpoints;
using Eidet.Service.Mcp;
using Eidet.Service.Tools;

namespace Eidet.Service.Api;

/// <summary>
/// HTTP listener for the local Eidet REST API + MCP-over-HTTP bridge. Owns the
/// <see cref="HttpListener"/>, the <see cref="ApiAuthGate"/>, the per-area
/// endpoint classes (memory/layers/usage/maintenance/enrich/meta/mcp), and the
/// <see cref="ApiRouter"/> that dispatches incoming requests to them.
/// </summary>
public class EidetApiServer
{
    private readonly ApiAuthGate _auth;
    private readonly ApiRouter _router;
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    private readonly MemoryReadEndpoints _memoryRead;
    private readonly MemoryWriteEndpoints _memoryWrite;
    private readonly MemoryBulkEndpoints _memoryBulk;
    private readonly LayerEndpoints _layerEndpoints;
    private readonly MaintenanceEndpoints _maintenanceEndpoints;
    private readonly QualityEndpoint _qualityEndpoint;
    private readonly ScheduledTasksEndpoint _scheduledTasksEndpoint;
    private readonly UsageEndpoints _usageEndpoints;
    private readonly EnrichEndpoint _enrichEndpoint;
    private readonly MetaEndpoints _meta;
    private readonly McpEndpoint _mcp;

    public EidetApiServer(MemoryService svc, IntakeService intake, ConsolidationEngine consolidation,
        IMaintenanceRunner maintenance, ExportService export, string bindAddress, int port,
        LayerService? layers = null, LayerSyncService? layerSync = null,
        McpServer? mcpServer = null, AuthConfig? auth = null,
        QualityService? quality = null, EnrichmentService? enrichment = null, EidetConfig? config = null,
        UsageTracker? usage = null, ScheduledTaskService? scheduledTasks = null)
    {
        _auth = new ApiAuthGate(auth ?? new AuthConfig());
        _baseUrl = $"http://{bindAddress}:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);

        var dispatcher = ToolDispatcherFactory.Create(svc, intake, consolidation, maintenance, export, layers, usage);

        _memoryRead = new MemoryReadEndpoints(svc, dispatcher, usage, layers);
        _memoryWrite = new MemoryWriteEndpoints(svc, dispatcher);
        _memoryBulk = new MemoryBulkEndpoints(dispatcher, export, usage);
        _layerEndpoints = new LayerEndpoints(layers, layerSync);
        _maintenanceEndpoints = new MaintenanceEndpoints(dispatcher);
        _qualityEndpoint = new QualityEndpoint(quality, usage);
        _scheduledTasksEndpoint = new ScheduledTasksEndpoint(scheduledTasks);
        _usageEndpoints = new UsageEndpoints(usage);
        _enrichEndpoint = new EnrichEndpoint(enrichment);
        _meta = new MetaEndpoints(svc, enrichment, config, _baseUrl, _startedAt);
        _mcp = new McpEndpoint(mcpServer, svc, intake, consolidation, maintenance);

        _router = BuildRouter();
    }

    public string BaseUrl => _baseUrl;

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = HandleRequestAsync(context, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var method = ctx.Request.HttpMethod;

            if (method == "OPTIONS")
            {
                HttpJson.AddCorsHeaders(ctx);
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            HttpJson.AddCorsHeaders(ctx);

            if (!await _auth.CheckAsync(ctx, method, path)) return;

            if (!await _router.DispatchAsync(ctx, method, path, ct))
                await HttpJson.WriteAsync(ctx, new { error = "Not found", hint = "Try /ui for the Web UI, or /api/health for the API." }, 404);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Eidet] Unhandled error: {ex}");
            EidetLog.Error($"Unhandled API error on {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}", ex);
            try { await HttpJson.WriteAsync(ctx, new { error = "Internal server error" }, 500); } catch { }
        }
    }

    private ApiRouter BuildRouter()
    {
        var r = new ApiRouter();

        // MCP-over-HTTP
        r.MapPost("/mcp", (ctx, _, ct) => _mcp.Handle(ctx, ct));

        // Meta
        r.MapGet("/api/health", (ctx, _, _) => _meta.Health(ctx));
        r.MapGet("/api/status", (ctx, _, ct) => _meta.Status(ctx, ct));

        // Memory — exact + non-id-prefixed
        r.MapGet("/api/eidet/context", (ctx, _, ct) => _memoryRead.GetContext(ctx, ct));
        r.Map("GET", p => p == "/api/eidet/recall" || p == "/api/eidet/search",
            (ctx, _, ct) => _memoryRead.Search(ctx, ct));
        r.MapPrefix("GET", "/api/eidet/history/", _memoryRead.History);
        r.MapGetPrefix("/api/eidet/stats", (ctx, _, ct) => _memoryRead.Stats(ctx, ct));
        r.MapPost("/api/eidet", (ctx, _, ct) => _memoryWrite.Store(ctx, ct));
        r.MapPost("/api/eidet/feedback", (ctx, _, ct) => _memoryWrite.Feedback(ctx, ct));
        r.MapPost("/api/eidet/intake", (ctx, _, ct) => _memoryBulk.Intake(ctx, ct));
        r.MapPost("/api/eidet/consolidate", (ctx, _, ct) => _memoryBulk.Consolidate(ctx, ct));
        r.MapPost("/api/maintenance", (ctx, _, ct) => _maintenanceEndpoints.Maintenance(ctx, ct));
        r.MapGet("/api/eidet/export", (ctx, _, ct) => _memoryBulk.Export(ctx, ct));
        r.MapPost("/api/eidet/packs/export", (ctx, _, ct) => _memoryBulk.PackExport(ctx, ct));
        r.MapPost("/api/eidet/packs/import", (ctx, _, ct) => _memoryBulk.PackImport(ctx, ct));
        r.MapPost("/api/eidet/links", (ctx, _, ct) => _memoryWrite.CreateLink(ctx, ct));
        r.MapGet("/api/eidet/links", (ctx, _, ct) => _memoryRead.GetLinks(ctx, ct));

        // Layers
        r.MapGet("/api/eidet/layers", (ctx, _, ct) => _layerEndpoints.GetLayers(ctx, ct));
        r.MapPost("/api/eidet/layers/mount", (ctx, _, ct) => _layerEndpoints.MountLayer(ctx, ct));
        r.MapPost("/api/eidet/layers/sync", (ctx, _, ct) => _layerEndpoints.LayerSync(ctx, ct));

        // Memory by id (must come after the more specific /api/eidet/* exact routes above)
        r.MapPrefix("PUT", "/api/eidet/", id => !id.Contains("/links"), _memoryWrite.UpdateMemory);
        r.Map("POST", p => p.EndsWith("/links") && p.StartsWith("/api/eidet/") && p != "/api/eidet/links",
            (ctx, path, ct) => _memoryWrite.AddMemoryLink(ctx, MemoryWriteEndpoints.ExtractMemoryIdFromLinkPath(path), ct));
        r.Map("DELETE", p => p.EndsWith("/links") && p.StartsWith("/api/eidet/") && p != "/api/eidet/links",
            (ctx, path, ct) => _memoryWrite.RemoveMemoryLink(ctx, MemoryWriteEndpoints.ExtractMemoryIdFromLinkPath(path), ct));
        r.MapPrefix("DELETE", "/api/eidet/layers/", _layerEndpoints.UnmountLayer);
        r.MapPrefix("DELETE", "/api/eidet/", _memoryWrite.Forget);

        // Quality / context preview / usage / enrich / scheduler / repos / browse / graph
        r.MapGet("/api/eidet/quality", (ctx, _, ct) => _qualityEndpoint.Quality(ctx, ct));
        r.MapGet("/api/eidet/context/preview", (ctx, _, ct) => _memoryRead.ContextPreview(ctx, ct));
        r.MapGet("/api/eidet/usage", (ctx, _, ct) => _usageEndpoints.Usage(ctx, ct));
        r.MapGet("/api/eidet/usage/timeseries", (ctx, _, ct) => _usageEndpoints.TimeSeries(ctx, ct));
        r.MapGet("/api/eidet/usage/hourly", (ctx, _, ct) => _usageEndpoints.Hourly(ctx, ct));
        r.MapPost("/api/eidet/enrich", (ctx, _, ct) => _enrichEndpoint.Enrich(ctx, ct));
        r.MapGet("/api/eidet/scheduled-tasks", (ctx, _, ct) => _scheduledTasksEndpoint.List(ctx, ct));
        r.MapGet("/api/eidet/repos", (ctx, _, ct) => _memoryRead.GetRepos(ctx, ct));
        r.MapGet("/api/eidet/browse", (ctx, _, ct) => _memoryRead.Browse(ctx, ct));
        r.MapGet("/api/eidet/graph", (ctx, _, ct) => _memoryRead.Graph(ctx, ct));

        // Catch-all GET memory by id (last GET under /api/eidet/)
        r.MapPrefix("GET", "/api/eidet/", _memoryRead.GetMemory);

        // Embedded Web UI + root
        r.MapAny(p => p == "/ui" || p == "/ui/", (ctx, _, _) => EmbeddedAssets.ServeAsync(ctx, "index.html"));
        r.MapAnyPrefix("/ui/", (ctx, asset, _) => EmbeddedAssets.ServeAsync(ctx, asset));
        r.MapAny(p => p == "/" || p == "", (ctx, _, _) => _meta.Root(ctx));

        return r;
    }
}
