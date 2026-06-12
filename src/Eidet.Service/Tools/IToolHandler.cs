using Eidet.Service.Mcp;

namespace Eidet.Service.Tools;

/// <summary>
/// One handler per logical Eidet tool. Owns argument binding, Core-service invocation, and the
/// translation of results into a transport-agnostic <see cref="ToolResult"/>. Schemas live with
/// the handler so adding a tool is a single class plus a single registration.
/// </summary>
public interface IToolHandler
{
    /// <summary>The MCP tool name (e.g., <c>eidet_store</c>). Also the dispatch key.</summary>
    string Name { get; }

    /// <summary>The UsageTracker operation label (e.g., <c>Store</c>).</summary>
    string UsageOp { get; }

    /// <summary>
    /// Whether this tool is advertised on the MCP surface (<c>tools/list</c>) and callable over MCP.
    /// Defaults to <c>true</c>. Advanced/maintenance tools opt out so the agent-facing surface stays
    /// to the core session flow; they remain available via the REST API and CLI, which use the full
    /// dispatcher regardless of this flag.
    /// </summary>
    bool McpExposed => true;

    /// <summary>MCP tool definition co-located with the handler. Used by tools/list.</summary>
    McpToolDefinition Schema { get; }

    Task<ToolResult> ExecuteAsync(ToolRequest request);
}
