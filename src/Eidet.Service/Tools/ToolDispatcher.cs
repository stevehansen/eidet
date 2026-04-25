using Eidet.Core;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools;

/// <summary>
/// Routes a <see cref="ToolRequest"/> to the matching <see cref="IToolHandler"/> and owns the
/// cross-cutting concerns no handler should re-implement: usage scoping, exception → status
/// mapping, error logging, and unknown-tool rejection.
/// </summary>
public sealed class ToolDispatcher
{
    private readonly Dictionary<string, IToolHandler> _handlers;
    private readonly UsageTracker? _usage;

    public ToolDispatcher(IEnumerable<IToolHandler> handlers, UsageTracker? usage = null)
    {
        _handlers = handlers.ToDictionary(h => h.Name, StringComparer.OrdinalIgnoreCase);
        _usage = usage;
    }

    public bool IsRegistered(string toolName) => _handlers.ContainsKey(toolName);

    public IReadOnlyCollection<IToolHandler> Handlers => _handlers.Values;

    public async Task<ToolResult> InvokeAsync(ToolRequest request)
    {
        if (!_handlers.TryGetValue(request.Tool, out var handler))
            return ToolResult.NotFound($"Unknown tool: {request.Tool}");

        using var scope = _usage?.StartScope(request.RepoId, handler.UsageOp);
        try
        {
            var result = await handler.ExecuteAsync(request);
            if (result.IsOk && result.ResultCount is { } count)
                scope?.SetResultCount(count);
            return result;
        }
        catch (MissingToolArgumentException ex)
        {
            return ToolResult.BadRequest($"Tool '{request.Tool}': {ex.Message}");
        }
        catch (Exception ex)
        {
            EidetLog.Error($"Tool '{request.Tool}' failed for repo '{request.RepoId}'", ex);
            return ToolResult.Internal($"Internal error ({ex.GetType().Name}): {ex.Message}");
        }
    }
}
