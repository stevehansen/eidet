using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Formatters;

/// <summary>
/// Renders a <see cref="ToolResult"/> as an MCP tool-call result. MCP only carries text, so this
/// projects <see cref="ToolResult.HumanSummary"/> and flips <c>isError</c> for any non-Ok status.
/// </summary>
public static class McpFormatter
{
    public static McpCallToolResult Format(ToolResult result)
    {
        var text = result.HumanSummary ?? FallbackText(result.Status);
        return result.IsOk ? McpCallToolResult.Text(text) : McpCallToolResult.Error(text);
    }

    private static string FallbackText(ToolStatus status) => status switch
    {
        ToolStatus.Ok => "OK",
        ToolStatus.NotFound => "Not found",
        ToolStatus.BadRequest => "Bad request",
        ToolStatus.Conflict => "Conflict",
        ToolStatus.Rejected => "Rejected",
        ToolStatus.Internal => "Internal error",
        _ => "Error",
    };
}
