using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core;
using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Service.Mcp;

namespace Eidet.Service.Api;

public class EidetApiServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly MemoryService _svc;
    private readonly IntakeService _intake;
    private readonly ConsolidationService _consolidation;
    private readonly MaintenanceService _maintenance;
    private readonly ExportService _export;
    private readonly QualityService? _quality;
    private readonly LayerService? _layers;
    private readonly LayerSyncService? _layerSync;
    private readonly McpServer? _mcpServer;
    private readonly ConcurrentDictionary<string, McpServer> _mcpServerPool = new();
    private readonly IEnrichmentService? _enrichment;
    private readonly EidetConfig? _config;
    private readonly AuthConfig _auth;
    private readonly UsageTracker? _usage;
    private readonly ScheduledTaskService? _scheduledTasks;
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public EidetApiServer(MemoryService svc, IntakeService intake, ConsolidationService consolidation,
        MaintenanceService maintenance, ExportService export, string bindAddress, int port,
        LayerService? layers = null, LayerSyncService? layerSync = null,
        McpServer? mcpServer = null, AuthConfig? auth = null,
        QualityService? quality = null, IEnrichmentService? enrichment = null, EidetConfig? config = null,
        UsageTracker? usage = null, ScheduledTaskService? scheduledTasks = null)
    {
        _svc = svc;
        _intake = intake;
        _consolidation = consolidation;
        _maintenance = maintenance;
        _export = export;
        _quality = quality;
        _layers = layers;
        _layerSync = layerSync;
        _mcpServer = mcpServer;
        _enrichment = enrichment;
        _config = config;
        _auth = auth ?? new AuthConfig();
        _usage = usage;
        _scheduledTasks = scheduledTasks;
        _baseUrl = $"http://{bindAddress}:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
    }

    public string BaseUrl => _baseUrl;

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = HandleRequestAsync(context, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            var method = ctx.Request.HttpMethod;

            // CORS preflight
            if (method == "OPTIONS")
            {
                AddCorsHeaders(ctx);
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            AddCorsHeaders(ctx);

            // Auth check
            if (_auth.Enabled)
            {
                var requiredScope = ApiKeyService.GetRequiredScope(method, path);
                if (!string.IsNullOrEmpty(requiredScope))
                {
                    var authHeader = ctx.Request.Headers["Authorization"];
                    var rawKey = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                        ? authHeader["Bearer ".Length..] : null;

                    if (string.IsNullOrEmpty(rawKey))
                    {
                        await WriteJson(ctx, new { error = "Authentication required" }, 401);
                        return;
                    }

                    var entry = ApiKeyService.ValidateKey(_auth, rawKey);
                    if (entry is null)
                    {
                        await WriteJson(ctx, new { error = "Invalid API key" }, 401);
                        return;
                    }

                    if (!ApiKeyService.HasScope(entry, requiredScope))
                    {
                        await WriteJson(ctx, new { error = "Insufficient permissions", required = requiredScope }, 403);
                        return;
                    }
                }
            }

            if (method == "POST" && path == "/mcp")
                await HandleMcpRequest(ctx, ct);

            else if (method == "GET" && path == "/api/health")
                await WriteJson(ctx, new { status = "ok", version = Eidet.Core.EidetVersion.Current });

            else if (method == "GET" && path == "/api/status")
                await HandleStatus(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/context")
                await HandleGetContext(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/search")
                await HandleSearch(ctx, ct);

            else if (method == "GET" && path.StartsWith("/api/eidet/history/"))
                await HandleHistory(ctx, path["/api/eidet/history/".Length..], ct);

            else if (method == "GET" && path.StartsWith("/api/eidet/stats"))
                await HandleStats(ctx, ct);

            else if (method == "POST" && path == "/api/eidet")
                await HandleStore(ctx, ct);

            else if (method == "POST" && path == "/api/eidet/feedback")
                await HandleFeedback(ctx, ct);

            else if (method == "POST" && path == "/api/eidet/intake")
                await HandleIntake(ctx, ct);

            else if (method == "POST" && path == "/api/eidet/consolidate")
                await HandleConsolidate(ctx, ct);

            else if (method == "POST" && path == "/api/maintenance")
                await HandleMaintenance(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/export")
                await HandleExport(ctx, ct);

            else if (method == "POST" && path == "/api/eidet/packs/export")
                await HandlePackExport(ctx, ct);

            else if (method == "POST" && path == "/api/eidet/packs/import")
                await HandlePackImport(ctx, ct);

            else if (method == "POST" && path == "/api/eidet/links")
                await HandleCreateLink(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/links")
                await HandleGetLinks(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/layers")
                await HandleGetLayers(ctx, ct);

            else if (method == "POST" && path == "/api/eidet/layers/mount")
                await HandleMountLayer(ctx, ct);

            else if (method == "POST" && path == "/api/eidet/layers/sync")
                await HandleLayerSync(ctx, ct);

            else if (method == "PUT" && path.StartsWith("/api/eidet/") && !path.Contains("/links"))
                await HandleUpdateMemory(ctx, path["/api/eidet/".Length..], ct);

            else if (method == "POST" && path.EndsWith("/links") && path.StartsWith("/api/eidet/") && path != "/api/eidet/links")
                await HandleAddMemoryLink(ctx, ExtractMemoryIdFromLinkPath(path), ct);

            else if (method == "DELETE" && path.EndsWith("/links") && path.StartsWith("/api/eidet/") && path != "/api/eidet/links")
                await HandleRemoveMemoryLink(ctx, ExtractMemoryIdFromLinkPath(path), ct);

            else if (method == "DELETE" && path.StartsWith("/api/eidet/layers/"))
                await HandleUnmountLayer(ctx, path["/api/eidet/layers/".Length..], ct);

            else if (method == "DELETE" && path.StartsWith("/api/eidet/"))
                await HandleForget(ctx, path["/api/eidet/".Length..], ct);

            else if (method == "GET" && path == "/api/eidet/quality")
                await HandleQuality(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/context/preview")
                await HandleContextPreview(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/usage")
                await HandleUsage(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/usage/timeseries")
                await HandleUsageTimeSeries(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/usage/hourly")
                await HandleUsageHourly(ctx, ct);

            else if (method == "POST" && path == "/api/eidet/enrich")
                await HandleEnrich(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/scheduled-tasks")
                await HandleScheduledTasks(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/repos")
                await HandleGetRepos(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/browse")
                await HandleBrowse(ctx, ct);

            else if (method == "GET" && path == "/api/eidet/graph")
                await HandleGraph(ctx, ct);

            else if (method == "GET" && path.StartsWith("/api/eidet/"))
                await HandleGetMemory(ctx, path["/api/eidet/".Length..], ct);

            else if (path == "/ui" || path == "/ui/")
                await ServeEmbeddedFile(ctx, "index.html");

            else if (path.StartsWith("/ui/"))
                await ServeEmbeddedFile(ctx, path["/ui/".Length..]);

            else if (path == "/" || path == "")
                await HandleRoot(ctx);

            else
                await WriteJson(ctx, new { error = "Not found", hint = "Try /ui for the Web UI, or /api/health for the API." }, 404);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Eidet] Unhandled error: {ex}");
            EidetLog.Error($"Unhandled API error on {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}", ex);
            try { await WriteJson(ctx, new { error = "Internal server error" }, 500); } catch { }
        }
    }

    private async Task HandleGetContext(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }
        using var scope = _usage?.StartScope(repo, "Context");
        var context = await _svc.GetContextAsync(repo, ct: ct);
        scope?.SetResultCount(1);
        await WriteJson(ctx, new { repo, context });
    }

    private async Task HandleSearch(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        var q = ctx.Request.QueryString["q"];
        if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(q))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' and 'q' parameters" }, 400);
            return;
        }

        var crossRepo = ctx.Request.QueryString["cross_repo"];
        var query = new MemoryQuery
        {
            Text = q,
            Limit = int.TryParse(ctx.Request.QueryString["limit"], out var lim) ? lim : 10,
            Type = Enum.TryParse<MemoryType>(ctx.Request.QueryString["type"], true, out var t) ? t : null,
            Tags = ctx.Request.QueryString["tags"]?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [],
            CrossRepo = string.Equals(crossRepo, "true", StringComparison.OrdinalIgnoreCase),
        };

        using var scope = _usage?.StartScope(repo, "Search");
        var results = await _svc.RecallAsync(repo, query, ct);
        scope?.SetResultCount(results.Count);
        await WriteJson(ctx, new { repo, query = q, results });
    }

    private async Task HandleGetMemory(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        var decoded = Uri.UnescapeDataString(id);
        var chain = await _svc.GetVersionChainAsync(decoded, ct);
        if (chain.Count == 0)
        {
            await WriteJson(ctx, new { error = "Memory not found" }, 404);
            return;
        }
        await WriteJson(ctx, chain[0]);
    }

    private async Task HandleStore(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await ReadJson<StoreRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Repo) || string.IsNullOrEmpty(req.Content))
        {
            await WriteJson(ctx, new { error = "Missing required fields: repo, content" }, 400);
            return;
        }

        using var scope = _usage?.StartScope(req.Repo, "Store");
        var result = await _svc.StoreAsync(
            repoId: req.Repo,
            content: req.Content,
            type: req.Type,
            tags: req.Tags,
            importance: req.Importance ?? 0.5f,
            source: req.Source ?? "claude-session",
            sessionId: req.SessionId,
            supersedes: req.Supersedes,
            ct: ct);

        if (!result.Success)
        {
            if (result.DuplicateId != null)
            {
                await WriteJson(ctx, new { error = result.Reason, duplicateId = result.DuplicateId }, 409);
                return;
            }
            await WriteJson(ctx, new { error = result.Reason }, 422);
            return;
        }

        scope?.SetResultCount(1);
        await WriteJson(ctx, new { id = result.Id }, 201);
    }

    private async Task HandleForget(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        var decoded = Uri.UnescapeDataString(id);
        var reason = ctx.Request.QueryString["reason"];
        var ok = await _svc.ForgetAsync(decoded, reason, ct: ct);

        if (ok) await WriteJson(ctx, new { forgotten = true });
        else await WriteJson(ctx, new { error = "Memory not found" }, 404);
    }

    private async Task HandleFeedback(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await ReadJson<FeedbackRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.MemoryId))
        {
            await WriteJson(ctx, new { error = "Missing required field: memoryId" }, 400);
            return;
        }

        // Extract repo from memory ID (format: memories/{repoSlug}/...)
        var repoHint = ExtractRepoFromMemoryId(req.MemoryId);
        using var scope = repoHint != null ? _usage?.StartScope(repoHint, "Feedback") : null;
        var ok = await _svc.ApplyFeedbackAsync(req.MemoryId, req.WasUsed, ct);
        if (ok) await WriteJson(ctx, new { applied = true });
        else await WriteJson(ctx, new { error = "Memory not found" }, 404);
    }

    private async Task HandleHistory(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        var decoded = Uri.UnescapeDataString(id);
        var chain = await _svc.GetVersionChainAsync(decoded, ct);
        await WriteJson(ctx, new { chain });
    }

    private async Task HandleStats(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }
        var context = await _svc.GetContextAsync(repo, maxTokens: 50, ct: ct);
        await WriteJson(ctx, new { repo, summary = context.Trim() });
    }

    private async Task HandleIntake(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        // Resolve the filesystem path: use explicit path param, the repo value if it looks
        // like a path, or look up the original path from the usage anchor document.
        var path = ctx.Request.QueryString["path"];
        if (string.IsNullOrEmpty(path))
            path = RepoUsage.LooksLikePath(repo) ? repo : null;
        if (string.IsNullOrEmpty(path) && _usage is not null)
            path = await _usage.GetOriginalPathAsync(repo);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            await WriteJson(ctx, new { error = $"Cannot resolve filesystem path for repo '{repo}'. The path '{path ?? "(unknown)"}' does not exist." }, 400);
            return;
        }

        using var scope = _usage?.StartScope(repo, "Intake");
        var result = await _intake.IngestAsync(repo, path, ct: ct);
        scope?.SetResultCount(result.NewCount);
        await WriteJson(ctx, new { newCount = result.NewCount, skippedCount = result.SkippedCount, dependencies = result.DetectedLinks.Count });
    }

    private async Task HandleConsolidate(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }
        using var scope = _usage?.StartScope(repo, "Consolidate");
        var result = await _consolidation.ConsolidateAsync(RepoIdNormalizer.Normalize(repo), ct: ct);
        scope?.SetResultCount(result.InsightsCreated + result.InsightsBoosted);
        await WriteJson(ctx, new { candidates = result.Candidates.Count, insightsCreated = result.InsightsCreated, insightsBoosted = result.InsightsBoosted });
    }

    private async Task HandleMaintenance(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }
        using var scope = _usage?.StartScope(repo, "Maintenance");
        var result = await _maintenance.RunAsync(RepoIdNormalizer.Normalize(repo), ct: ct);
        await WriteJson(ctx, result);
    }

    private async Task HandleStatus(HttpListenerContext ctx, CancellationToken ct)
    {
        var info = await _svc.GetStoreInfoAsync(ct);

        // Check Ollama health if enrichment is configured
        object? ollamaStatus = null;
        if (_config?.Enrichment.OllamaEnabled == true && _enrichment != null)
        {
            var healthy = await _enrichment.CheckHealthAsync(ct);
            ollamaStatus = new
            {
                enabled = true,
                healthy,
                model = _config.Enrichment.OllamaModel,
                url = _config.Enrichment.OllamaUrl,
            };
        }
        else if (_config != null)
        {
            ollamaStatus = new { enabled = false };
        }

        await WriteJson(ctx, new
        {
            version = Eidet.Core.EidetVersion.Current,
            status = "running",
            uptime = (DateTime.UtcNow - _startedAt).ToString(@"d\.hh\:mm\:ss"),
            api = _baseUrl,
            database = info,
            ollama = ollamaStatus,
        });
    }

    private async Task HandlePackExport(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await ReadJson<PackExportRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Repo) || string.IsNullOrEmpty(req.BundleId)
            || string.IsNullOrEmpty(req.Name) || string.IsNullOrEmpty(req.Version))
        {
            await WriteJson(ctx, new { error = "Missing required fields: repo, bundleId, name, version" }, 400);
            return;
        }

        var pack = await _export.ExportPackAsync(
            RepoIdNormalizer.Normalize(req.Repo), req.BundleId, req.Name, req.Version, "user",
            ct: ct);

        if (!string.IsNullOrEmpty(req.OutputPath))
        {
            await _export.ExportPackToFileAsync(pack, req.OutputPath, ct);
            await WriteJson(ctx, new { entries = pack.Entries.Count, path = req.OutputPath });
        }
        else
        {
            await WriteJson(ctx, pack);
        }
    }

    private async Task HandlePackImport(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await ReadJson<PackImportRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Path))
        {
            await WriteJson(ctx, new { error = "Missing required field: path" }, 400);
            return;
        }

        var pack = await _export.ImportPackFromFileAsync(req.Path, ct);
        var count = await _export.ImportPackAsync(pack, ct);
        await WriteJson(ctx, new { imported = count, bundle = pack.Name, version = pack.Version });
    }

    private async Task HandleCreateLink(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = await ReadJson<CreateLinkRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Repo) || string.IsNullOrEmpty(req.TargetRepo)
            || string.IsNullOrEmpty(req.Relation))
        {
            await WriteJson(ctx, new { error = "Missing required fields: repo, targetRepo, relation" }, 400);
            return;
        }

        var normalizedRepoId = RepoIdNormalizer.Normalize(req.Repo);
        var targetRepoId = RepoIdNormalizer.Normalize(req.TargetRepo);
        var content = $"Cross-repo link: {req.Relation} -> {targetRepoId}";

        var result = await _svc.StoreAsync(normalizedRepoId, content, Eidet.Core.Domain.MemoryType.Insight,
            tags: ["cross-repo-link", req.Relation], importance: 0.7f, source: "user", ct: ct);

        if (result.Success)
            await WriteJson(ctx, new { id = result.Id, from = normalizedRepoId, to = targetRepoId, relation = req.Relation }, 201);
        else
            await WriteJson(ctx, new { error = result.Reason }, 422);
    }

    private async Task HandleGetLinks(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var query = new Eidet.Core.Domain.MemoryQuery
        {
            Text = "cross-repo link",
            Tags = ["cross-repo-link"],
            Limit = 50,
        };
        var results = await _svc.RecallAsync(repo, query, ct);
        await WriteJson(ctx, new { repo, links = results });
    }

    private async Task HandleExport(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }
        var markdown = await _export.ExportMarkdownAsync(RepoIdNormalizer.Normalize(repo), ct);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/markdown";
        var bytes = System.Text.Encoding.UTF8.GetBytes(markdown);
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
    }

    private async Task HandleMcpRequest(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_mcpServer is null)
        {
            await WriteJson(ctx, new { error = "MCP server not available" }, 501);
            return;
        }

        // Support per-request repo override via query string (used by container overlays)
        var repoOverride = ctx.Request.QueryString["repo"];
        var server = string.IsNullOrEmpty(repoOverride)
            ? _mcpServer
            : _mcpServerPool.GetOrAdd(repoOverride, id =>
                new McpServer(_svc, _intake, _consolidation, _maintenance, id));

        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct);

        var response = await server.ProcessRequestAsync(body, ct);

        if (response is null)
        {
            // Notification — no response needed (204 No Content)
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.OutputStream, response, McpServer.SerializerOptions, ct);
        ctx.Response.Close();
    }

    private async Task HandleGetLayers(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_layers is null) { await WriteJson(ctx, new { error = "Layer service not available" }, 501); return; }
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo)) { await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400); return; }
        var layers = await _layers.GetApplicableLayersAsync(RepoIdNormalizer.Normalize(repo), ct: ct);
        await WriteJson(ctx, new { repo, layers });
    }

    private async Task HandleMountLayer(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_layers is null) { await WriteJson(ctx, new { error = "Layer service not available" }, 501); return; }
        var req = await ReadJson<MountLayerRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.LayerId) || string.IsNullOrEmpty(req.Name))
        {
            await WriteJson(ctx, new { error = "Missing required fields: layerId, name" }, 400);
            return;
        }
        var layer = await _layers.MountAsync(req.LayerId, req.Name, req.Type,
            req.ApplicableRepos, req.ApplicablePackages, req.SourcePath, ct: ct);
        await WriteJson(ctx, layer, 201);
    }

    private async Task HandleLayerSync(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_layerSync is null) { await WriteJson(ctx, new { error = "Layer sync service not available" }, 501); return; }
        var req = await ReadJson<LayerSyncRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Path))
        {
            await WriteJson(ctx, new { error = "Missing required field: path" }, 400);
            return;
        }

        if (req.Preview == true)
        {
            var preview = await _layerSync.PreviewAsync(req.Path, req.LayerId, ct);
            await WriteJson(ctx, preview);
        }
        else
        {
            var result = await _layerSync.SyncAsync(req.Path, req.LayerId, req.RemoveStale ?? true, ct);
            await WriteJson(ctx, result);
        }
    }

    private async Task HandleUnmountLayer(HttpListenerContext ctx, string layerId, CancellationToken ct)
    {
        if (_layers is null) { await WriteJson(ctx, new { error = "Layer service not available" }, 501); return; }
        var decoded = Uri.UnescapeDataString(layerId);
        var ok = await _layers.UnmountAsync(decoded, ct);
        if (ok) await WriteJson(ctx, new { unmounted = true });
        else await WriteJson(ctx, new { error = "Layer not found" }, 404);
    }

    private async Task HandleGetRepos(HttpListenerContext ctx, CancellationToken ct)
    {
        var repos = await _svc.GetRepoIdsAsync(ct);
        var pathMap = _usage is not null
            ? await _usage.GetAllRepoPathsAsync()
            : new Dictionary<string, string?>();
        await WriteJson(ctx, new
        {
            repos = repos.Select(r => new
            {
                repoId = r,
                originalPath = pathMap.TryGetValue(r, out var p) ? p : null,
            })
        });
    }

    private async Task HandleBrowse(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }
        var skip = int.TryParse(ctx.Request.QueryString["skip"], out var s) ? s : 0;
        var take = int.TryParse(ctx.Request.QueryString["take"], out var t) ? t : 50;
        var type = Enum.TryParse<MemoryType>(ctx.Request.QueryString["type"], true, out var mt) ? mt : (MemoryType?)null;

        using var scope = _usage?.StartScope(repo, "Browse");
        var entries = await _svc.BrowseAsync(repo, skip, take, type, ct);
        scope?.SetResultCount(entries.Count);
        await WriteJson(ctx, new { repo, skip, take, count = entries.Count, entries });
    }

    private async Task HandleGraph(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }
        var limit = int.TryParse(ctx.Request.QueryString["limit"], out var lim) ? lim : 200;
        using var scope = _usage?.StartScope(repo, "Graph");
        var graph = await _svc.GetGraphDataAsync(repo, limit, ct);
        scope?.SetResultCount(graph.Nodes.Count);
        await WriteJson(ctx, graph);
    }

    private async Task HandleQuality(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_quality is null) { await WriteJson(ctx, new { error = "Quality service not available" }, 503); return; }
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo)) { await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400); return; }
        using var scope = _usage?.StartScope(repo, "Quality");
        var report = await _quality.AnalyzeAsync(repo, ct);
        await WriteJson(ctx, report);
    }

    private async Task HandleScheduledTasks(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_scheduledTasks is null)
        {
            await WriteJson(ctx, new { error = "Scheduler not available" }, 503);
            return;
        }

        var tasks = await _scheduledTasks.GetTasksAsync(ct);
        await WriteJson(ctx, new { tasks });
    }

    private async Task HandleContextPreview(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400);
            return;
        }

        var maxTokens = int.TryParse(ctx.Request.QueryString["tokens"], out var t) ? t : 600;
        var contextText = await _svc.GetContextAsync(repo, maxTokens, ct);

        // Gather cross-repo scope info
        List<object>? layerInfo = null;
        List<string>? scope = null;
        if (_layers != null)
        {
            var normalizedRepoId = RepoIdNormalizer.Normalize(repo);
            var layers = await _layers.GetApplicableLayersAsync(normalizedRepoId, ct: ct);
            layerInfo = layers.Select(l => (object)new { l.Id, l.Name, type = l.Type.ToString() }).ToList();
            scope = await _layers.ResolveScopeAsync(normalizedRepoId, crossRepo: true, ct: ct);
        }

        await WriteJson(ctx, new
        {
            repo,
            maxTokens,
            context = contextText.Trim(),
            estimatedTokens = (int)Math.Ceiling(contextText.Length / 4.0),
            layers = layerInfo,
            crossRepoScope = scope,
        });
    }

    private async Task HandleRoot(HttpListenerContext ctx)
    {
        var userAgent = ctx.Request.Headers["User-Agent"] ?? "";
        var isBrowser = userAgent.Contains("Mozilla/") || userAgent.Contains("Chrome/")
            || userAgent.Contains("Safari/") || userAgent.Contains("Edge/");

        if (isBrowser)
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Add("Location", "/ui");
            ctx.Response.Close();
            return;
        }

        await WriteJson(ctx, new
        {
            service = "Eidet Memory Service",
            version = Eidet.Core.EidetVersion.Current,
            endpoints = new
            {
                ui = "/ui",
                health = "/api/health",
                status = "/api/status",
                docs = "https://github.com/stevehansen/eidet",
            },
        });
    }

    private async Task HandleUsage(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_usage is null) { await WriteJson(ctx, new { error = "Usage tracking not available" }, 503); return; }
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo)) { await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400); return; }
        var days = int.TryParse(ctx.Request.QueryString["days"], out var d) ? d : 30;
        var report = await _usage.GetUsageAsync(repo, DateTime.UtcNow.AddDays(-days));
        await WriteJson(ctx, report);
    }

    private async Task HandleUsageTimeSeries(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_usage is null) { await WriteJson(ctx, new { error = "Usage tracking not available" }, 503); return; }
        var repo = ctx.Request.QueryString["repo"];
        var op = ctx.Request.QueryString["operation"];
        if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(op))
        {
            await WriteJson(ctx, new { error = "Missing 'repo' and 'operation' parameters" }, 400);
            return;
        }
        var days = int.TryParse(ctx.Request.QueryString["days"], out var d) ? d : 30;
        var data = await _usage.GetTimeSeriesAsync(repo, op, DateTime.UtcNow.AddDays(-days));
        await WriteJson(ctx, new { repo, operation = op, data });
    }

    private async Task HandleUsageHourly(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_usage is null) { await WriteJson(ctx, new { error = "Usage tracking not available" }, 503); return; }
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo)) { await WriteJson(ctx, new { error = "Missing 'repo' parameter" }, 400); return; }
        var days = int.TryParse(ctx.Request.QueryString["days"], out var d) ? d : 7;
        var buckets = await _usage.GetHourlyBreakdownAsync(repo, days);
        await WriteJson(ctx, new { repo, days, buckets });
    }

    private async Task HandleEnrich(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_enrichment is null || !_enrichment.IsAvailable)
        {
            await WriteJson(ctx, new { error = "Enrichment service not available. Configure Ollama in eidet setup." }, 503);
            return;
        }

        var req = await ReadJson<EnrichRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Content) || string.IsNullOrEmpty(req.Task))
        {
            await WriteJson(ctx, new { error = "Missing required fields: content, task" }, 400);
            return;
        }

        try
        {
            string? result = req.Task switch
            {
                "oneliner" => await _enrichment.GenerateOneLinerAsync(req.Content, ct),
                "summary" => await _enrichment.GenerateSummaryAsync(req.Content, ct),
                "foresight" => await _enrichment.GenerateForesightHintAsync(req.Content, ct),
                "entities" => string.Join(", ", await _enrichment.ExtractEntitiesAsync(req.Content, ct)),
                _ => null,
            };

            if (result is null)
                await WriteJson(ctx, new { error = $"Unknown task: {req.Task}. Use: oneliner, summary, foresight, entities" }, 400);
            else
                await WriteJson(ctx, new { task = req.Task, result });
        }
        catch (Exception ex)
        {
            await WriteJson(ctx, new { error = $"Enrichment failed: {ex.Message}" }, 500);
        }
    }

    private async Task HandleUpdateMemory(HttpListenerContext ctx, string id, CancellationToken ct)
    {
        var decoded = Uri.UnescapeDataString(id);
        var req = await ReadJson<UpdateMemoryRequest>(ctx);
        if (req is null)
        {
            await WriteJson(ctx, new { error = "Invalid request body" }, 400);
            return;
        }

        MemoryType? type = null;
        if (!string.IsNullOrEmpty(req.Type) && Enum.TryParse<MemoryType>(req.Type, true, out var t))
            type = t;

        var ok = await _svc.UpdateMemoryAsync(
            decoded,
            content: req.Content,
            tags: req.Tags,
            importance: req.Importance,
            confidence: req.Confidence,
            type: type,
            oneLiner: req.OneLiner,
            summary: req.Summary,
            foresightHint: req.ForesightHint,
            ct: ct);

        if (ok) await WriteJson(ctx, new { updated = true, id = decoded });
        else await WriteJson(ctx, new { error = "Memory not found or update rejected" }, 404);
    }

    private async Task HandleAddMemoryLink(HttpListenerContext ctx, string memoryId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(memoryId))
        {
            await WriteJson(ctx, new { error = "Invalid memory ID in path" }, 400);
            return;
        }
        var decoded = Uri.UnescapeDataString(memoryId);
        var req = await ReadJson<AddMemoryLinkRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.TargetRepoId) || string.IsNullOrEmpty(req.Relation))
        {
            await WriteJson(ctx, new { error = "Missing required fields: targetRepoId, relation" }, 400);
            return;
        }

        var ok = await _svc.AddLinkAsync(decoded, req.TargetRepoId, req.Relation, req.TargetMemoryId, ct);
        if (ok) await WriteJson(ctx, new { linked = true }, 201);
        else await WriteJson(ctx, new { error = "Memory not found" }, 404);
    }

    private async Task HandleRemoveMemoryLink(HttpListenerContext ctx, string memoryId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(memoryId))
        {
            await WriteJson(ctx, new { error = "Invalid memory ID in path" }, 400);
            return;
        }
        var decoded = Uri.UnescapeDataString(memoryId);
        var targetRepo = ctx.Request.QueryString["targetRepoId"];
        var relation = ctx.Request.QueryString["relation"];
        if (string.IsNullOrEmpty(targetRepo) || string.IsNullOrEmpty(relation))
        {
            await WriteJson(ctx, new { error = "Missing query params: targetRepoId, relation" }, 400);
            return;
        }

        var ok = await _svc.RemoveLinkAsync(decoded, targetRepo, relation, ct);
        if (ok) await WriteJson(ctx, new { removed = true });
        else await WriteJson(ctx, new { error = "Link or memory not found" }, 404);
    }

    /// <summary>
    /// Extract memory ID from paths like /api/eidet/{memoryId}/links.
    /// Memory IDs contain slashes (memories/repoSlug/type/hash), so we take everything between /api/eidet/ and /links.
    /// </summary>
    private static string ExtractMemoryIdFromLinkPath(string path)
    {
        var prefix = "/api/eidet/";
        var suffix = "/links";
        if (path.StartsWith(prefix) && path.EndsWith(suffix) && path.Length > prefix.Length + suffix.Length)
            return path[prefix.Length..^suffix.Length];
        return "";
    }

    private static string? ExtractRepoFromMemoryId(string memoryId)
    {
        // Memory IDs follow: memories/{repoSlug}/{type}/{hash}
        if (!memoryId.StartsWith("memories/", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = memoryId.Split('/');
        return parts.Length >= 3 ? parts[1].Replace("--", ":\\").Replace('-', '\\') : null;
    }

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".json"] = "application/json",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
    };

    private static async Task ServeEmbeddedFile(HttpListenerContext ctx, string filePath)
    {
        // Sanitize path
        filePath = filePath.Replace('\\', '/').TrimStart('/');
        if (filePath.Contains(".."))
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        var resourceName = $"Eidet.Service.wwwroot.{filePath.Replace('/', '.')}";
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var ext = Path.GetExtension(filePath);
        ctx.Response.ContentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");
        ctx.Response.StatusCode = 200;

        // For index.html: replace __VERSION__ placeholder for cache busting
        if (filePath == "index.html")
        {
            using var reader = new StreamReader(stream);
            var html = await reader.ReadToEndAsync();
            html = html.Replace("__VERSION__", Eidet.Core.EidetVersion.Current);
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        else
        {
            // Static assets: cache for 1 year (cache busted by ?v=version in index.html)
            if (ext is ".css" or ".js" or ".png" or ".svg")
                ctx.Response.Headers.Add("Cache-Control", "public, max-age=31536000, immutable");

            ctx.Response.ContentLength64 = stream.Length;
            await stream.CopyToAsync(ctx.Response.OutputStream);
        }

        ctx.Response.Close();
    }

    private static void AddCorsHeaders(HttpListenerContext ctx)
    {
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
        ctx.Response.Headers.Add("Access-Control-Max-Age", "86400");
    }

    private static async Task WriteJson(HttpListenerContext ctx, object data, int statusCode = 200)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.OutputStream, data, JsonOptions);
        ctx.Response.Close();
    }

    private static async Task<T?> ReadJson<T>(HttpListenerContext ctx) where T : class
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}

public record StoreRequest
{
    public string Repo { get; init; } = "";
    public string Content { get; init; } = "";
    public MemoryType Type { get; init; }
    public List<string>? Tags { get; init; }
    public float? Importance { get; init; }
    public string? Source { get; init; }
    public string? SessionId { get; init; }
    public string? Supersedes { get; init; }
}

public record FeedbackRequest
{
    public string MemoryId { get; init; } = "";
    public bool WasUsed { get; init; }
}

public record PackExportRequest
{
    public string Repo { get; init; } = "";
    public string BundleId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string? OutputPath { get; init; }
}

public record PackImportRequest
{
    public string Path { get; init; } = "";
}

public record CreateLinkRequest
{
    public string Repo { get; init; } = "";
    public string TargetRepo { get; init; } = "";
    public string Relation { get; init; } = "";
}

public record MountLayerRequest
{
    public string LayerId { get; init; } = "";
    public string Name { get; init; } = "";
    public LayerType Type { get; init; }
    public List<string>? ApplicableRepos { get; init; }
    public List<string>? ApplicablePackages { get; init; }
    public string? SourcePath { get; init; }
}

public record LayerSyncRequest
{
    public string Path { get; init; } = "";
    public string? LayerId { get; init; }
    public bool? Preview { get; init; }
    public bool? RemoveStale { get; init; }
}

public record UpdateMemoryRequest
{
    public string? Content { get; init; }
    public List<string>? Tags { get; init; }
    public float? Importance { get; init; }
    public float? Confidence { get; init; }
    public string? Type { get; init; }
    public string? OneLiner { get; init; }
    public string? Summary { get; init; }
    public string? ForesightHint { get; init; }
}

public record AddMemoryLinkRequest
{
    public string TargetRepoId { get; init; } = "";
    public string? TargetMemoryId { get; init; }
    public string Relation { get; init; } = "";
}

public record EnrichRequest
{
    public string Content { get; init; } = "";
    public string Task { get; init; } = "";  // oneliner, summary, foresight, entities
}
