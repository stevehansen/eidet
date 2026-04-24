using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core;
using Eidet.Core.Domain;
using Eidet.Core.Maintenance;
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
    private readonly IntakeService _intake;
    private readonly ConsolidationEngine _consolidation;
    private readonly IMaintenanceRunner _maintenance;
    private readonly UsageTracker? _usage;
    private readonly ExportService? _export;
    private readonly LayerService? _layers;
    private readonly string _repoId;
    private readonly bool _autoIntake;
    private bool _autoIntakeDone;

    public McpServer(MemoryService svc, IntakeService intake, ConsolidationEngine consolidation,
        IMaintenanceRunner maintenance, string repoId, bool autoIntake = true,
        UsageTracker? usage = null, ExportService? export = null, LayerService? layers = null)
    {
        _svc = svc;
        _intake = intake;
        _consolidation = consolidation;
        _maintenance = maintenance;
        _repoId = repoId;
        _autoIntake = autoIntake;
        _usage = usage;
        _export = export;
        _layers = layers;
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

    /// <summary>
    /// Handle a single JSON-RPC request string. Used by both stdio and HTTP transports.
    /// </summary>
    public async Task<JsonRpcResponse?> ProcessRequestAsync(string json, CancellationToken ct) =>
        await HandleJsonRpcAsync(json, ct);

    /// <summary>
    /// JSON serializer options (shared with HTTP transport).
    /// </summary>
    public static JsonSerializerOptions SerializerOptions => JsonOptions;

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

    private static readonly Dictionary<string, string> ToolToOperation = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eidet_store"] = "Store",
        ["eidet_recall"] = "Recall",
        ["eidet_context"] = "Context",
        ["eidet_forget"] = "Forget",
        ["eidet_feedback"] = "Feedback",
        ["eidet_history"] = "History",
        ["eidet_intake"] = "Intake",
        ["eidet_link"] = "Store",
        ["eidet_consolidate"] = "Consolidate",
        ["eidet_maintenance"] = "Maintenance",
        ["eidet_edit"] = "Store",
        ["eidet_pack_export"] = "PackExport",
        ["eidet_pack_import"] = "PackImport",
    };

    private async Task<McpCallToolResult> ExecuteToolAsync(string name, JsonElement args, CancellationToken ct)
    {
        var opName = ToolToOperation.GetValueOrDefault(name);
        using var scope = opName != null ? _usage?.StartScope(_repoId, opName) : null;
        try
        {
            var result = name switch
            {
                "eidet_store" => await ExecuteStore(args, ct),
                "eidet_recall" => await ExecuteRecall(args, ct),
                "eidet_context" => await ExecuteContext(args, ct),
                "eidet_forget" => await ExecuteForget(args, ct),
                "eidet_feedback" => await ExecuteFeedback(args, ct),
                "eidet_history" => await ExecuteHistory(args, ct),
                "eidet_intake" => await ExecuteIntake(args, ct),
                "eidet_link" => await ExecuteLink(args, ct),
                "eidet_consolidate" => await ExecuteConsolidate(args, ct),
                "eidet_maintenance" => await ExecuteMaintenance(args, ct),
                "eidet_edit" => await ExecuteEdit(args, ct),
                "eidet_pack_export" => await ExecutePackExport(args, ct),
                "eidet_pack_import" => await ExecutePackImport(args, ct),
                _ => McpCallToolResult.Error($"Unknown tool: {name}"),
            };
            return result;
        }
        catch (MissingMcpArgumentException ex)
        {
            return McpCallToolResult.Error($"Tool '{name}': {ex.Message}");
        }
        catch (Exception ex)
        {
            EidetLog.Error($"MCP tool '{name}' failed for repo '{_repoId}'", ex);
            return McpCallToolResult.Error($"Internal error ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private async Task<McpCallToolResult> ExecuteStore(JsonElement args, CancellationToken ct)
    {
        var content = RequireString(args, "content");
        var typeStr = RequireString(args, "type");
        if (!Enum.TryParse<MemoryType>(typeStr, true, out var type))
            return McpCallToolResult.Error($"Invalid type: {typeStr}. Use: observation, insight, procedure, heuristic.");

        var tags = GetStringArray(args, "tags");
        var importance = GetFloat(args, "importance", 0.5f);
        var supersedes = GetString(args, "supersedes");
        var provenanceStr = GetString(args, "provenance");
        MemoryProvenance? provenance = provenanceStr switch
        {
            null => null,
            var s when s.Equals("Bundle", StringComparison.OrdinalIgnoreCase) => MemoryProvenance.Pack,
            var s when Enum.TryParse<MemoryProvenance>(s, true, out var p) => p,
            _ => null,
        };

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
            Text = RequireString(args, "query"),
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
        // Auto-intake on first context call if no memories exist for THIS repo
        if (_autoIntake && !_autoIntakeDone)
        {
            _autoIntakeDone = true;
            try
            {
                var normalizedRepoId = RepoIdNormalizer.Normalize(_repoId);
                var counts = await _svc.GetCountsByTypeAsync(normalizedRepoId, ct);
                var totalForRepo = counts.Values.Sum();
                if (totalForRepo == 0)
                {
                    await _intake.IngestAsync(_repoId, _repoId, dryRun: false, ct: ct);
                }
            }
            catch { /* Non-critical — don't fail context for auto-intake issues */ }
        }

        var maxTokens = GetInt(args, "max_tokens", 600);
        var context = await _svc.GetContextAsync(_repoId, maxTokens, ct);
        return McpCallToolResult.Text(context);
    }

    private async Task<McpCallToolResult> ExecuteForget(JsonElement args, CancellationToken ct)
    {
        var id = RequireString(args, "id");
        var reason = GetString(args, "reason");
        var ok = await _svc.ForgetAsync(id, reason, ct: ct);
        return ok ? McpCallToolResult.Text($"Memory {id} has been invalidated.") : McpCallToolResult.Error($"Memory not found: {id}");
    }

    private async Task<McpCallToolResult> ExecuteFeedback(JsonElement args, CancellationToken ct)
    {
        var id = RequireString(args, "id");
        var used = RequireBool(args, "used");
        var ok = await _svc.ApplyFeedbackAsync(id, used, ct);
        var label = used ? "Echo" : "Fizzle";
        return ok ? McpCallToolResult.Text($"{label} feedback applied to {id}.") : McpCallToolResult.Error($"Memory not found: {id}");
    }

    private async Task<McpCallToolResult> ExecuteHistory(JsonElement args, CancellationToken ct)
    {
        var id = RequireString(args, "id");
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

    private async Task<McpCallToolResult> ExecuteIntake(JsonElement args, CancellationToken ct)
    {
        var dryRun = GetBool(args, "dry_run");
        var path = GetString(args, "path");

        IntakeResult result;
        if (!string.IsNullOrEmpty(path))
        {
            var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(_repoId, path);
            if (!Directory.Exists(resolvedPath))
                return McpCallToolResult.Error($"Directory not found: {resolvedPath}");

            var pattern = GetString(args, "pattern") ?? "*.md";
            var recursive = GetBool(args, "recursive", true);
            var importance = GetFloat(args, "importance", 0.6f);
            var extraTags = GetStringArray(args, "tags");

            result = await _intake.IngestDocsAsync(resolvedPath, resolvedPath, recursive, pattern, importance,
                extraTags.Count > 0 ? extraTags : null, dryRun, ct);
        }
        else
        {
            var projectPath = Path.IsPathRooted(_repoId) ? _repoId : Directory.GetCurrentDirectory();
            result = await _intake.IngestAsync(_repoId, projectPath, dryRun, ct);
        }

        var mode = dryRun ? "Would ingest" : "Ingested";
        var lines = new List<string> { $"{mode}: {result.NewCount} new, {result.SkippedCount} skipped" };

        if (result.ProducedPackages.Count > 0)
            lines.Add($"Produces: {string.Join(", ", result.ProducedPackages)}");
        if (result.DetectedLinks.Count > 0)
            lines.Add($"Dependencies: {string.Join(", ", result.DetectedLinks.Select(l => l.TargetRepoId))}");

        foreach (var item in result.Items.Take(20))
        {
            var status = item.WasSkipped ? $"[SKIP: {item.SkipReason}]" : "[NEW]";
            lines.Add($"  {status} {item.Source} -> {item.Type}: {Truncate(item.Content, 80)}");
        }
        if (result.Items.Count > 20)
            lines.Add($"  ... and {result.Items.Count - 20} more");

        return McpCallToolResult.Text(string.Join("\n", lines));
    }

    private async Task<McpCallToolResult> ExecuteLink(JsonElement args, CancellationToken ct)
    {
        var targetRepo = GetString(args, "target_repo");
        if (string.IsNullOrEmpty(targetRepo)) return McpCallToolResult.Error("Missing: target_repo");
        var relation = GetString(args, "relation");
        if (string.IsNullOrEmpty(relation)) return McpCallToolResult.Error("Missing: relation");

        var targetRepoId = RepoIdNormalizer.Normalize(targetRepo);
        var targetMemoryId = GetString(args, "target_memory_id");
        var normalizedRepoId = RepoIdNormalizer.Normalize(_repoId);

        var content = targetMemoryId != null
            ? $"Memory link: {relation} -> {targetMemoryId}"
            : $"Cross-repo link: {relation} -> {targetRepoId}";

        var now = DateTime.UtcNow;
        var result = await _svc.StoreAsync(normalizedRepoId, content, MemoryType.Insight,
            tags: targetMemoryId != null ? ["memory-link", relation] : ["cross-repo-link", relation],
            importance: 0.7f, source: "claude-session", ct: ct);

        return result.Success
            ? McpCallToolResult.Text($"Link created: {normalizedRepoId} --[{relation}]--> {targetRepoId} (ID: {result.Id})")
            : McpCallToolResult.Error(result.Reason!);
    }

    private async Task<McpCallToolResult> ExecuteConsolidate(JsonElement args, CancellationToken ct)
    {
        var dryRun = GetBool(args, "dry_run");
        var normalizedRepoId = RepoIdNormalizer.Normalize(_repoId);
        var result = await _consolidation.ConsolidateAsync(normalizedRepoId, dryRun, ct);

        if (result.Candidates.Count == 0)
            return McpCallToolResult.Text("No consolidation candidates found. Need 3+ related observations.");

        var lines = new List<string>();
        if (dryRun)
            lines.Add($"Consolidation preview: {result.Candidates.Count} candidate(s)");
        else
        {
            var parts = new List<string>();
            if (result.InsightsCreated > 0) parts.Add($"{result.InsightsCreated} created");
            if (result.InsightsBoosted > 0) parts.Add($"{result.InsightsBoosted} boosted");
            lines.Add($"Consolidated: {string.Join(", ", parts)} from {result.Candidates.Count} group(s)");
        }

        foreach (var c in result.Candidates)
        {
            lines.Add($"\n  Group ({c.ObservationIds.Count} observations):");
            lines.Add($"  -> {Truncate(c.ProposedContent, 100)}");
            lines.Add($"  Tags: {string.Join(", ", c.Tags)} | Importance: {c.ProposedImportance:F2}");
        }

        return McpCallToolResult.Text(string.Join("\n", lines));
    }

    private async Task<McpCallToolResult> ExecuteMaintenance(JsonElement args, CancellationToken ct)
    {
        var normalizedRepoId = RepoIdNormalizer.Normalize(_repoId);
        var isActive = _svc.IsRepoActive(normalizedRepoId);
        var result = await _maintenance.RunAsync(
            new MaintenanceRequest { RepoId = normalizedRepoId, IsRepoActive = isActive }, ct);
        return McpCallToolResult.Text(result.ToString());
    }

    private async Task<McpCallToolResult> ExecuteEdit(JsonElement args, CancellationToken ct)
    {
        var id = RequireString(args, "id");
        var content = GetString(args, "content");
        var tags = GetStringArray(args, "tags");
        var importance = args.TryGetProperty("importance", out var imp) && imp.ValueKind == JsonValueKind.Number ? (float?)imp.GetSingle() : null;
        var confidence = args.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number ? (float?)conf.GetSingle() : null;
        var typeStr = GetString(args, "type");
        MemoryType? type = typeStr != null && Enum.TryParse<MemoryType>(typeStr, true, out var t) ? t : null;

        var ok = await _svc.UpdateMemoryAsync(id,
            content: content,
            tags: tags.Count > 0 ? tags : null,
            importance: importance,
            confidence: confidence,
            type: type,
            ct: ct);

        return ok
            ? McpCallToolResult.Text($"Memory {id} updated successfully.")
            : McpCallToolResult.Error($"Memory not found or update rejected: {id}");
    }

    private async Task<McpCallToolResult> ExecutePackExport(JsonElement args, CancellationToken ct)
    {
        if (_export == null)
            return McpCallToolResult.Error("Pack export not available in this context.");

        var packId = GetString(args, "pack_id") ?? GetString(args, "bundle_id")
            ?? throw new MissingMcpArgumentException("pack_id");
        var name = GetString(args, "name") ?? packId;
        var version = GetString(args, "version") ?? "1.0.0";
        var author = GetString(args, "author") ?? "";
        var description = GetString(args, "description");
        var output = GetString(args, "output") ?? $"{packId}.md";
        var packages = GetStringArray(args, "packages");
        var tags = GetStringArray(args, "tags");

        var typeStrs = GetStringArray(args, "types");
        List<MemoryType>? types = typeStrs.Count > 0
            ? typeStrs.Where(t => Enum.TryParse<MemoryType>(t, true, out _))
                .Select(t => Enum.Parse<MemoryType>(t, true)).ToList()
            : null;

        var normalizedRepoId = RepoIdNormalizer.Normalize(_repoId);
        var pack = await _export.ExportPackAsync(normalizedRepoId, packId, name, version, author,
            types: types, tags: tags.Count > 0 ? tags : null,
            applicablePackages: packages.Count > 0 ? packages : null, ct: ct);
        pack.Description = description;

        // Resolve output path relative to repo dir
        var outputPath = Path.IsPathRooted(output) ? output : Path.Combine(_repoId, output);
        await _export.ExportPackToFileAsync(pack, outputPath, ct);

        var format = outputPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "markdown" : "JSON";
        return McpCallToolResult.Text($"Exported {pack.Entries.Count} memories as {format} pack to {outputPath}");
    }

    private async Task<McpCallToolResult> ExecutePackImport(JsonElement args, CancellationToken ct)
    {
        if (_export == null)
            return McpCallToolResult.Error("Pack import not available in this context.");

        var path = RequireString(args, "path");
        var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(_repoId, path);

        if (!File.Exists(resolvedPath))
            return McpCallToolResult.Error($"File not found: {resolvedPath}");

        var pack = await _export.ImportPackFromFileAsync(resolvedPath, ct);

        if (_layers != null)
        {
            var (imported, layer) = await _export.ImportPackWithLayerAsync(pack, _layers, ct);
            return McpCallToolResult.Text($"Imported {imported} memories from \"{pack.Name}\" v{pack.Version}. Mounted as layer: {layer.Name}");
        }
        else
        {
            var imported = await _export.ImportPackAsync(pack, ct);
            return McpCallToolResult.Text($"Imported {imported} memories from \"{pack.Name}\" v{pack.Version}.");
        }
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

    private static string RequireString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new MissingMcpArgumentException(name);
        var s = v.GetString();
        if (string.IsNullOrEmpty(s))
            throw new MissingMcpArgumentException(name);
        return s;
    }

    private static bool RequireBool(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var v) || (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False))
            throw new MissingMcpArgumentException(name);
        return v.GetBoolean();
    }

    private sealed class MissingMcpArgumentException(string field)
        : Exception($"missing required argument '{field}'")
    {
        public string Field { get; } = field;
    }

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
        Core.StringUtils.Truncate(s, maxLen);
}
