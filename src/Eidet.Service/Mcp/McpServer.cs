using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Domain;
using Eidet.Core.Services;

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
    private readonly string _repoId;

    public McpServer(MemoryService svc, string repoId)
    {
        _svc = svc;
        _repoId = repoId;
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

            var response = await HandleJsonRpcAsync(line, ct);
            if (response != null)
            {
                var json = JsonSerializer.Serialize(response, JsonOptions);
                Console.WriteLine(json);
                Console.Out.Flush();
            }
        }
    }

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

    private static JsonRpcResponse HandleToolsList(JsonRpcRequest request)
    {
        return JsonRpcResponse.Success(request.Id, new McpToolsListResult
        {
            Tools = McpToolDefinitions.GetAll(),
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
        try
        {
            return name switch
            {
                "eidet_store" => await ExecuteStore(args, ct),
                "eidet_recall" => await ExecuteRecall(args, ct),
                "eidet_context" => await ExecuteContext(args, ct),
                "eidet_forget" => await ExecuteForget(args, ct),
                "eidet_feedback" => await ExecuteFeedback(args, ct),
                "eidet_history" => await ExecuteHistory(args, ct),
                _ => McpCallToolResult.Error($"Unknown tool: {name}"),
            };
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"Internal error: {ex.Message}");
        }
    }

    private async Task<McpCallToolResult> ExecuteStore(JsonElement args, CancellationToken ct)
    {
        var content = args.GetProperty("content").GetString()!;
        var typeStr = args.GetProperty("type").GetString()!;
        if (!Enum.TryParse<MemoryType>(typeStr, true, out var type))
            return McpCallToolResult.Error($"Invalid type: {typeStr}. Use: observation, insight, procedure, heuristic.");

        var tags = GetStringArray(args, "tags");
        var importance = GetFloat(args, "importance", 0.5f);
        var supersedes = GetString(args, "supersedes");
        var provenanceStr = GetString(args, "provenance");
        MemoryProvenance? provenance = provenanceStr != null && Enum.TryParse<MemoryProvenance>(provenanceStr, true, out var p) ? p : null;

        var result = await _svc.StoreAsync(_repoId, content, type, tags, importance,
            source: "claude-session", supersedes: supersedes, provenance: provenance, ct: ct);

        if (result.DuplicateId != null)
            return McpCallToolResult.Text($"Near-duplicate of existing memory: {result.DuplicateId}");
        if (!result.Success)
            return McpCallToolResult.Error(result.Reason!);
        return McpCallToolResult.Text($"Stored: {result.Id}");
    }

    private async Task<McpCallToolResult> ExecuteRecall(JsonElement args, CancellationToken ct)
    {
        var query = new MemoryQuery
        {
            Text = args.GetProperty("query").GetString()!,
            Type = GetEnum<MemoryType>(args, "type"),
            Tags = GetStringArray(args, "tags"),
            Limit = GetInt(args, "limit", 10),
            IncludeExpired = GetBool(args, "include_expired"),
            CrossRepo = GetBool(args, "cross_repo", defaultValue: true),
        };

        var results = await _svc.RecallAsync(_repoId, query, ct);

        if (results.Count == 0)
            return McpCallToolResult.Text("No memories found.");

        var lines = new List<string> { $"{results.Count} memory(ies) found:" };
        foreach (var r in results)
        {
            var prefix = r.Type switch
            {
                MemoryType.Insight => "[I]",
                MemoryType.Observation => "[O]",
                MemoryType.Procedure => "[P]",
                MemoryType.Heuristic => "[H]",
                _ => "[?]",
            };
            var stale = r.StalenessWarning != null ? $" {r.StalenessWarning}" : "";
            var display = r.OneLiner ?? r.Summary ?? Truncate(r.Content, 120);
            lines.Add($"  {prefix} {display}{stale}");
            lines.Add($"      id={r.Id} importance={r.Importance:F2} score={r.Score:F2}");
        }

        return McpCallToolResult.Text(string.Join("\n", lines));
    }

    private async Task<McpCallToolResult> ExecuteContext(JsonElement args, CancellationToken ct)
    {
        var maxTokens = GetInt(args, "max_tokens", 600);
        var context = await _svc.GetContextAsync(_repoId, maxTokens, ct);
        return McpCallToolResult.Text(context);
    }

    private async Task<McpCallToolResult> ExecuteForget(JsonElement args, CancellationToken ct)
    {
        var id = args.GetProperty("id").GetString()!;
        var reason = GetString(args, "reason");
        var ok = await _svc.ForgetAsync(id, reason, ct: ct);
        return ok ? McpCallToolResult.Text($"Memory {id} has been invalidated.") : McpCallToolResult.Error($"Memory not found: {id}");
    }

    private async Task<McpCallToolResult> ExecuteFeedback(JsonElement args, CancellationToken ct)
    {
        var id = args.GetProperty("id").GetString()!;
        var used = args.GetProperty("used").GetBoolean();
        var ok = await _svc.ApplyFeedbackAsync(id, used, ct);
        var label = used ? "Echo" : "Fizzle";
        return ok ? McpCallToolResult.Text($"{label} feedback applied to {id}.") : McpCallToolResult.Error($"Memory not found: {id}");
    }

    private async Task<McpCallToolResult> ExecuteHistory(JsonElement args, CancellationToken ct)
    {
        var id = args.GetProperty("id").GetString()!;
        var chain = await _svc.GetVersionChainAsync(id, ct);

        if (chain.Count == 0)
            return McpCallToolResult.Error($"Memory not found: {id}");

        var lines = new List<string> { $"Version history for {id} ({chain.Count} version(s)):" };
        for (var i = 0; i < chain.Count; i++)
        {
            var e = chain[i];
            var current = i == 0 ? " (current)" : "";
            var expired = e.Validity.ValidUntil != null ? $" [expired: {e.ForgetReason ?? "superseded"}]" : "";
            lines.Add($"  {i + 1}. {e.Id}{current}{expired}");
            lines.Add($"     Created: {e.CreatedAt:u} | Importance: {e.Importance:F2}");
            lines.Add($"     {Truncate(e.Content, 100)}");
        }

        return McpCallToolResult.Text(string.Join("\n", lines));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static string? GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement args, string name, int defaultValue = 0) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : defaultValue;

    private static float GetFloat(JsonElement args, string name, float defaultValue = 0f) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : defaultValue;

    private static bool GetBool(JsonElement args, string name, bool defaultValue = false) =>
        args.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : defaultValue;

    private static List<string> GetStringArray(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
    }

    private static T? GetEnum<T>(JsonElement args, string name) where T : struct, Enum
    {
        var s = GetString(args, name);
        return s != null && Enum.TryParse<T>(s, true, out var v) ? v : null;
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
}
