using System.Text.Json.Nodes;
using Eidet.Core.LooseEnds;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Parks a Loose End through <see cref="LooseEndService.ParkAsync(ParkOptions, CancellationToken)"/>.
/// Secret-scanned but not signal-gated — terse, speculative notes are the point. Rejection → 422.
/// </summary>
public sealed class ParkToolHandler : IToolHandler
{
    private readonly LooseEndService _svc;

    public ParkToolHandler(LooseEndService svc) => _svc = svc;

    public string Name => "eidet_park";
    public string UsageOp => "Park";

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_park",
        Description = "Park an open todo to revisit later. Stores a Loose End that won't decay or auto-expire until you resolve it. Use for terse mid-task notes ('possible bug in retry logic, revisit').",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var args = request.Arguments;

        var note = ToolArgs.RequireString(args, "note");
        var tags = ToolArgs.GetStringArray(args, "tags");
        var priority = ToolArgs.GetInt(args, "priority", 2);

        var result = await _svc.ParkAsync(new ParkOptions(request.RepoId, note)
        {
            Tags = tags.Count > 0 ? tags : null,
            Priority = priority,
        }, request.Ct);

        if (!result.Success)
            return ToolResult.Rejected(result.Reason!);

        return ToolResult.Ok(
            payload: new { id = result.Id, state = "open" },
            summary: $"Parked: {result.Id}",
            count: 1);
    }

    private static JsonObject BuildSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["note"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Terse note describing the open work to revisit (e.g. 'flaky test in auth path, revisit retry logic').",
            },
            ["tags"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Tags used for recall ride-along matching.",
                ["items"] = new JsonObject { ["type"] = "string" },
            },
            ["priority"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Wake-up ordering: 1 high, 2 normal (default), 3 low.",
            },
        },
        ["required"] = new JsonArray { "note" },
    };
}
