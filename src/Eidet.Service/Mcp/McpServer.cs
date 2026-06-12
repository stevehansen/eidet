using System.Text.Json;
using Eidet.Core;
using Eidet.Core.Maintenance;
using Eidet.Core.Services;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Mcp;

public class McpServer
{
    private readonly string _repoId;
    private readonly ToolDispatcher _dispatcher;
    private readonly HashSet<string> _exposedTools;
    private readonly JsonRpcDispatcher _rpc;
    private readonly AutoIntakeOnContext? _autoIntake;

    public McpServer(MemoryService svc, IntakeService intake, ConsolidationEngine consolidation,
        IMaintenanceRunner maintenance, string repoId, bool autoIntake = true,
        UsageTracker? usage = null, ExportService? export = null, LayerService? layers = null)
    {
        _repoId = repoId;
        _dispatcher = ToolDispatcherFactory.Create(svc, intake, consolidation, maintenance, export, layers, usage);
        _exposedTools = _dispatcher.Handlers.Where(h => h.McpExposed).Select(h => h.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _autoIntake = autoIntake ? new AutoIntakeOnContext(svc, intake, repoId) : null;

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
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EidetLog.Error("[mcp] stdin read failed", ex);
                break;
            }

            if (line == null) break; // EOF
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonRpcResponse? response;
            try
            {
                response = await _rpc.DispatchAsync(line, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EidetLog.Error($"[mcp] Dispatch failed (line len={line.Length})", ex);
                response = JsonRpcResponse.ErrorResponse(null, -32603, $"Internal error: {ex.Message}");
            }

            if (response != null)
            {
                try
                {
                    var json = JsonSerializer.Serialize(response, JsonRpcDispatcher.SerializerOptions);
                    Console.WriteLine(json);
                    Console.Out.Flush();
                }
                catch (Exception ex)
                {
                    EidetLog.Error("[mcp] Failed to write response to stdout", ex);
                    break;
                }
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
            Tools = _dispatcher.Handlers.Where(h => h.McpExposed).Select(h => h.Schema).ToList(),
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

        if (string.IsNullOrEmpty(toolName))
            return JsonRpcResponse.ErrorResponse(request.Id, -32602, "Invalid params: expected name and arguments");

        if (!_exposedTools.Contains(toolName))
            return JsonRpcResponse.ErrorResponse(request.Id, -32601, $"Unknown tool: {toolName}");

        if (_autoIntake is not null)
        {
            try
            {
                await _autoIntake.OnToolCalledAsync(toolName, ct);
            }
            catch (Exception ex)
            {
                EidetLog.Error($"[mcp] AutoIntake failed for tool '{toolName}'", ex);
            }
        }

        var dispatched = await _dispatcher.InvokeAsync(new ToolRequest(toolName, _repoId, args, "mcp", ct));
        return JsonRpcResponse.Success(request.Id, McpFormatter.Format(dispatched));
    }
}
