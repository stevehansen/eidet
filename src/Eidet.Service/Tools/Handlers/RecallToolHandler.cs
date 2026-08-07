using System.Text.Json.Nodes;
using Eidet.Core.Domain;
using Eidet.Core.LooseEnds;
using Eidet.Core.Services;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tools.Handlers;

/// <summary>
/// Hybrid recall through <see cref="MemoryService.RecallAsync(string, RecallOptions, CancellationToken)"/>.
/// Layer scope is resolved inside <c>MemoryService</c>, so this handler is pure transport.
/// Returns the scored result list as the structured payload and a one-line-per-hit summary for MCP.
/// </summary>
public sealed class RecallToolHandler : IToolHandler
{
    private readonly MemoryService _svc;
    private readonly LooseEndService? _looseEnds;

    public RecallToolHandler(MemoryService svc, LooseEndService? looseEnds = null)
    {
        _svc = svc;
        _looseEnds = looseEnds;
    }

    public string Name => "eidet_recall";
    public string UsageOp => "Recall";

    public McpToolDefinition Schema { get; } = new()
    {
        Name = "eidet_recall",
        Description = "Search memories using hybrid retrieval (vector similarity + full-text + metadata filters). Returns scored results with staleness warnings.",
        InputSchema = BuildSchema(),
    };

    public async Task<ToolResult> ExecuteAsync(ToolRequest request)
    {
        var args = request.Arguments;

        var queryText = ToolArgs.RequireString(args, "query");
        var opts = new RecallOptions(queryText)
        {
            Type = ToolArgs.GetEnum<MemoryType>(args, "type"),
            Valence = ToolArgs.GetEnum<Valence>(args, "valence"),
            Stage = ToolArgs.GetEnum<FunctionalStage>(args, "stage"),
            Tags = ToolArgs.GetStringArray(args, "tags"),
            Limit = ToolArgs.GetInt(args, "limit", 10),
            IncludeExpired = ToolArgs.GetBool(args, "include_expired"),
            CrossRepo = ToolArgs.GetBool(args, "cross_repo", defaultValue: true),
        };

        var results = await _svc.RecallAsync(request.RepoId, opts, request.Ct);

        // Recall ride-along: open Loose Ends whose tags overlap the query surface in a SEPARATE
        // section, never mixed into the relevance-ranked memory list (the ride-along surface,
        // docs/domains/looseends.md).
        IReadOnlyList<LooseEnd> looseEnds = _looseEnds is not null && opts.Tags is { Count: > 0 }
            ? await _looseEnds.RideAlongAsync(request.RepoId, opts.Tags, request.Ct)
            : [];
        // Trim to a stable ride-along view — the raw LooseEnd carries internal lifecycle/source
        // fields (State, Resolution, SourceSessionId, …) that don't belong on the recall wire.
        var rideAlong = looseEnds
            .Select(le => new RideAlongView(le.Id, le.Note, le.Priority, le.Tags))
            .ToList();

        if (results.Count == 0 && rideAlong.Count == 0)
            return ToolResult.Ok(
                payload: new { repo = request.RepoId, query = queryText, results = Array.Empty<MemorySearchResult>(), looseEnds = rideAlong },
                summary: "No memories found.",
                count: 0);

        var lines = new List<string>();
        if (results.Count > 0)
        {
            lines.Add($"{results.Count} memory(ies) found:");
            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                var prefix = r.Type switch
                {
                    MemoryType.Insight => "[I]",
                    MemoryType.Observation => "[O]",
                    MemoryType.Procedure => "[P]",
                    MemoryType.Heuristic => "[H]",
                    _ => "[?]",
                };
                var stale = r.StalenessWarning != null ? $" {r.StalenessWarning}" : "";
                var glyph = r.Valence switch
                {
                    Valence.Refuting => "✗ ",
                    Valence.Cautionary => "⚠ ",
                    _ => "",
                };

                var headline = FirstNonEmpty(r.OneLiner, r.Summary) ?? Truncate(r.Content, HeadlineChars);
                lines.Add($"  {prefix} {glyph}{headline}{stale}");
                lines.Add($"      id={r.Id} importance={r.Importance:F2} score={r.Score:F2}");

                // Depth for the hits most likely to be acted on. A one-liner is a ~12-word LLM
                // abstraction of the memory; it reliably drops the class names, thresholds and file
                // paths that make a memory usable, so rendering it ALONE hands the agent a topic
                // label instead of the knowledge. Below the cut we stay terse: those hits are for
                // deciding whether to widen the query, not for acting on.
                if (i < DetailedHits)
                {
                    var body = r.Content.Trim();
                    // Suppress only when the headline already IS the content — otherwise the
                    // abstraction and the source say different things and both earn their space.
                    if (body.Length > 0 && !string.Equals(body, headline, StringComparison.Ordinal))
                        lines.Add($"      {Truncate(body, DetailChars).ReplaceLineEndings("\n      ")}");
                }
            }
        }

        if (rideAlong.Count > 0)
        {
            lines.Add($"{rideAlong.Count} open loose end(s) matching your tags:");
            foreach (var le in rideAlong)
                lines.Add($"  [~] {le.Note} (id={le.Id}, priority={le.Priority})");
        }

        return ToolResult.Ok(
            payload: new { repo = request.RepoId, query = queryText, results, looseEnds = rideAlong },
            summary: string.Join("\n", lines),
            count: results.Count);
    }

    /// <summary>How many top-ranked hits render their full content rather than just an abstraction.</summary>
    private const int DetailedHits = 3;

    /// <summary>Per-hit content budget for the detailed band (~175 tokens each).</summary>
    private const int DetailChars = 700;

    private const int HeadlineChars = 120;

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c)) return c.Trim();
        return null;
    }

    /// <summary>Trimmed recall ride-along projection — only the fields the agent needs to act on a
    /// matching open Loose End, deliberately excluding internal lifecycle/source fields.</summary>
    private sealed record RideAlongView(string Id, string Note, int Priority, IReadOnlyList<string> Tags);

    private static JsonObject BuildSchema()
    {
        var props = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Natural language search query.",
            },
            ["type"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Filter by type: observation, insight, procedure, heuristic.",
            },
            ["valence"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "neutral", "affirming", "refuting", "cautionary" },
                ["description"] = "Filter by stance: refuting (dead-ends), cautionary (warnings), affirming, or neutral.",
            },
            ["stage"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "analyze", "locate", "edit", "test", "debug", "deploy" },
                ["description"] = "Hard-filter by functional subtask before ranking. Returns memories tagged with this stage PLUS stage-agnostic memories; excludes memories tagged with a different stage.",
            },
            ["tags"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Filter by tags (AND logic).",
                ["items"] = new JsonObject { ["type"] = "string" },
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Max results 1-50 (default 10).",
            },
            ["include_expired"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Include forgotten/expired memories (default false).",
            },
            ["cross_repo"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Search linked repos and layers (default true).",
            },
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray { "query" },
        };
    }
}
