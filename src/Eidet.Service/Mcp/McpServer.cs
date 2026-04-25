using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Mcp;

public class McpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly MemoryService _svc;
    private readonly IntakeService _intake;
    private readonly string _repoId;
    private readonly bool _autoIntake;
    private readonly ToolDispatcher _dispatcher;
    private bool _autoIntakeDone;

    public McpServer(MemoryService svc, IntakeService intake, ConsolidationEngine consolidation,
        IMaintenanceRunner maintenance, string repoId, bool autoIntake = true,
        UsageTracker? usage = null, ExportService? export = null, LayerService? layers = null)
    {
        _svc = svc;
        _intake = intake;
        _repoId = repoId;
        _autoIntake = autoIntake;
        _dispatcher = BuildDispatcher(svc, consolidation, intake, maintenance, export, layers, usage);
    }

    private static ToolDispatcher BuildDispatcher(
        MemoryService svc, ConsolidationEngine consolidation, IntakeService intake,
        IMaintenanceRunner maintenance, ExportService? export, LayerService? layers,
        UsageTracker? usage) =>
        new([
            new StoreToolHandler(svc),
            new RecallToolHandler(svc),
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

    public async Task RunStdioAsync(CancellationToken ct)
    {
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break; // EOF

            if (string.IsNullOrWhiteSpace(line)) continue;

            var response = await HandleJsonRpcAsync(line, ct);
            if (response != null)
            {
                var json = JsonSerializer.Serialize(response, JsonOptions);
                Console.WriteLine(json);
                Console.Out.Flush();
            }
        }
    }

    /// <summary>
    /// Handle a single JSON-RPC request string. Used by both stdio and HTTP transports.
    /// </summary>
    public async Task<JsonRpcResponse?> ProcessRequestAsync(string json, CancellationToken ct) =>
        await HandleJsonRpcAsync(json, ct);

    /// <summary>
    /// JSON serializer options (shared with HTTP transport).
    /// </summary>
    public static JsonSerializerOptions SerializerOptions => JsonOptions;

    private async Task<JsonRpcResponse?> HandleJsonRpcAsync(string json, CancellationToken ct)
    {
        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<JsonRpcRequest>(json, JsonOptions);
        }
        catch
        {
            return JsonRpcResponse.ErrorResponse(null, -32700, "Parse error");
        }

        if (request == null || string.IsNullOrEmpty(request.Method))
            return JsonRpcResponse.ErrorResponse(null, -32600, "Invalid request");

        return request.Method switch
        {
            "initialize" => HandleInitialize(request),
            "notifications/initialized" => null, // No response for notifications
            "tools/list" => HandleToolsList(request),
            "tools/call" => await HandleToolsCallAsync(request, ct),
            _ => JsonRpcResponse.ErrorResponse(request.Id, -32601, $"Method not found: {request.Method}"),
        };
    }

    private static JsonRpcResponse HandleInitialize(JsonRpcRequest request)
    {
        return JsonRpcResponse.Success(request.Id, new McpInitializeResult
        {
            Instructions = "Eidet provides long-term memory for AI coding agents. Use eidet_context at session start for compact context, eidet_recall to search memories, eidet_store to save observations/insights/procedures/heuristics, and eidet_feedback to improve recall quality.",
        });
    }

    private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
    {
        return JsonRpcResponse.Success(request.Id, new McpToolsListResult
        {
            Tools = _dispatcher.Handlers.Select(h => h.Schema).ToList(),
        });
    }

    private async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request, CancellationToken ct)
    {
        if (request.Params == null)
            return JsonRpcResponse.ErrorResponse(request.Id, -32602, "Missing params");

        string toolName;
        JsonElement args;
        try
        {
            toolName = request.Params.Value.GetProperty("name").GetString()!;
            args = request.Params.Value.GetProperty("arguments");
        }
        catch
        {
            return JsonRpcResponse.ErrorResponse(request.Id, -32602, "Invalid params: expected name and arguments");
        }

        var result = await ExecuteToolAsync(toolName, args, ct);
        return JsonRpcResponse.Success(request.Id, result);
    }

    private async Task<McpCallToolResult> ExecuteToolAsync(string name, JsonElement args, CancellationToken ct)
    {
        // MCP-only side effect: first eidet_context call auto-ingests this repo if it has no memories.
        if (name == "eidet_context")
            await TryAutoIntakeAsync(ct);

        var dispatched = await _dispatcher.InvokeAsync(new ToolRequest(name, _repoId, args, "mcp", ct));
        return McpFormatter.Format(dispatched);
    }

    private async Task TryAutoIntakeAsync(CancellationToken ct)
    {
        if (!_autoIntake || _autoIntakeDone) return;
        _autoIntakeDone = true;
        try
        {
            var normalizedRepoId = RepoIdNormalizer.Normalize(_repoId);
            var counts = await _svc.GetCountsByTypeAsync(normalizedRepoId, ct);
            var totalForRepo = counts.Values.Sum();
            if (totalForRepo == 0)
                await _intake.IngestAsync(_repoId, _repoId, dryRun: false, ct: ct);
        }
        catch { /* Non-critical — don't fail context for auto-intake issues */ }
    }

}
