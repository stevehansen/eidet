using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Mcp;

public class McpServer
{
    private readonly MemoryService _svc;
    private readonly IntakeService _intake;
    private readonly string _repoId;
    private readonly bool _autoIntake;
    private readonly ToolDispatcher _dispatcher;
    private readonly JsonRpcDispatcher _rpc;
    private bool _autoIntakeDone;

    public McpServer(MemoryService svc, IntakeService intake, ConsolidationEngine consolidation,
        IMaintenanceRunner maintenance, string repoId, bool autoIntake = true,
        UsageTracker? usage = null, ExportService? export = null, LayerService? layers = null)
    {
        _svc = svc;
        _intake = intake;
        _repoId = repoId;
        _autoIntake = autoIntake;
        _dispatcher = ToolDispatcherFactory.Create(svc, intake, consolidation, maintenance, export, layers, usage);

        _rpc = new JsonRpcDispatcher(new Dictionary<string, JsonRpcDispatcher.Handler>
        {
            ["initialize"] = (req, _) => Task.FromResult<JsonRpcResponse?>(HandleInitialize(req)),
            ["notifications/initialized"] = (_, _) => Task.FromResult<JsonRpcResponse?>(null),
            ["tools/list"] = (req, _) => Task.FromResult<JsonRpcResponse?>(HandleToolsList(req)),
            ["tools/call"] = async (req, ct) => await HandleToolsCallAsync(req, ct),
        });
    }

    public async Task RunStdioAsync(CancellationToken ct)
    {
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break; // EOF

            if (string.IsNullOrWhiteSpace(line)) continue;

            var response = await _rpc.DispatchAsync(line, ct);
            if (response != null)
            {
                var json = JsonSerializer.Serialize(response, JsonRpcDispatcher.SerializerOptions);
                Console.WriteLine(json);
                Console.Out.Flush();
            }
        }
    }

    /// <summary>
    /// Handle a single JSON-RPC request string. Used by the HTTP transport.
    /// </summary>
    public Task<JsonRpcResponse?> ProcessRequestAsync(string json, CancellationToken ct) =>
        _rpc.DispatchAsync(json, ct);

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
