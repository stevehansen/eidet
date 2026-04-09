using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Domain;
using Eidet.Core.Services;

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
    private readonly HttpListener _listener;
    private readonly string _baseUrl;

    public EidetApiServer(MemoryService svc, string bindAddress, int port)
    {
        _svc = svc;
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

            if (method == "GET" && path == "/api/health")
                await WriteJson(ctx, new { status = "ok", version = Eidet.Core.EidetVersion.Current });

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

            else if (method == "DELETE" && path.StartsWith("/api/eidet/"))
                await HandleForget(ctx, path["/api/eidet/".Length..], ct);

            else if (method == "GET" && path.StartsWith("/api/eidet/"))
                await HandleGetMemory(ctx, path["/api/eidet/".Length..], ct);

            else
                await WriteJson(ctx, new { error = "Not found" }, 404);
        }
        catch (Exception ex)
        {
            try { await WriteJson(ctx, new { error = ex.Message }, 500); } catch { }
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
        var context = await _svc.GetContextAsync(repo, ct: ct);
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

        var query = new MemoryQuery
        {
            Text = q,
            Limit = int.TryParse(ctx.Request.QueryString["limit"], out var lim) ? lim : 10,
            Type = Enum.TryParse<MemoryType>(ctx.Request.QueryString["type"], true, out var t) ? t : null,
            Tags = ctx.Request.QueryString["tags"]?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [],
        };

        var results = await _svc.RecallAsync(repo, query, ct);
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
