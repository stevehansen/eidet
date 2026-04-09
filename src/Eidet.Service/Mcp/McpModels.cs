using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Eidet.Service.Mcp;

public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string Jsonrpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public JsonElement? Id { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }
}

public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")] public string Jsonrpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public JsonElement? Id { get; set; }
    [JsonPropertyName("result")] public object? Result { get; set; }
    [JsonPropertyName("error")] public JsonRpcError? Error { get; set; }

    public static JsonRpcResponse Success(JsonElement? id, object result) => new() { Id = id, Result = result };
    public static JsonRpcResponse ErrorResponse(JsonElement? id, int code, string message) => new()
    {
        Id = id,
        Error = new JsonRpcError { Code = code, Message = message },
    };
}

public class JsonRpcError
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public class McpInitializeResult
{
    [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; set; } = "2025-03-26";
    [JsonPropertyName("capabilities")] public McpCapabilities Capabilities { get; set; } = new();
    [JsonPropertyName("serverInfo")] public McpServerInfo ServerInfo { get; set; } = new();
    [JsonPropertyName("instructions")] public string? Instructions { get; set; }
}

public class McpCapabilities
{
    [JsonPropertyName("tools")] public McpToolsCapability? Tools { get; set; } = new();
}

public class McpToolsCapability
{
    [JsonPropertyName("listChanged")] public bool ListChanged { get; set; }
}

public class McpServerInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "eidet";
    [JsonPropertyName("version")] public string Version { get; set; } = Eidet.Core.EidetVersion.Current;
}

public class McpToolsListResult
{
    [JsonPropertyName("tools")] public List<McpToolDefinition> Tools { get; set; } = [];
}

public class McpToolDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("inputSchema")] public JsonObject InputSchema { get; set; } = new();
}

public class McpCallToolResult
{
    [JsonPropertyName("content")] public List<McpContent> Content { get; set; } = [];
    [JsonPropertyName("isError")] public bool IsError { get; set; }

    public static McpCallToolResult Text(string text) => new() { Content = [new McpContent { Text = text }] };
    public static McpCallToolResult Error(string text) => new() { Content = [new McpContent { Text = text }], IsError = true };
}

public class McpContent
{
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}
