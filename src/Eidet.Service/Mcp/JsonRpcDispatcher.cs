using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eidet.Service.Mcp;

/// <summary>
/// JSON-RPC 2.0 envelope: parses incoming JSON, dispatches to a registered
/// method handler, and wraps results/errors into <see cref="JsonRpcResponse"/>.
/// Transport-agnostic — used by both the stdio loop and the HTTP bridge.
/// </summary>
public sealed class JsonRpcDispatcher
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public delegate Task<JsonRpcResponse?> Handler(JsonRpcRequest request, CancellationToken ct);

    private readonly IReadOnlyDictionary<string, Handler> _handlers;

    public JsonRpcDispatcher(IReadOnlyDictionary<string, Handler> handlers)
    {
        _handlers = handlers;
    }

    /// <summary>
    /// Parse a JSON-RPC request line and route it to the matching handler.
    /// Returns <c>null</c> for notifications (no response should be written).
    /// </summary>
    public async Task<JsonRpcResponse?> DispatchAsync(string json, CancellationToken ct)
    {
        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<JsonRpcRequest>(json, SerializerOptions);
        }
        catch
        {
            return JsonRpcResponse.ErrorResponse(null, -32700, "Parse error");
        }

        if (request is null || string.IsNullOrEmpty(request.Method))
            return JsonRpcResponse.ErrorResponse(null, -32600, "Invalid request");

        if (!_handlers.TryGetValue(request.Method, out var handler))
            return JsonRpcResponse.ErrorResponse(request.Id, -32601, $"Method not found: {request.Method}");

        return await handler(request, ct);
    }
}
