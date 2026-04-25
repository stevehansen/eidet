using System.Net;
using Eidet.Core;
using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;
using Eidet.Core.Services;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// "About this service" routes: <c>/api/health</c>, <c>/api/status</c> (db + Ollama
/// health, uptime), and the bare <c>/</c> root which redirects browsers to the Web UI
/// and serves a service descriptor JSON for everything else.
/// </summary>
internal sealed class MetaEndpoints
{
    private readonly MemoryService _svc;
    private readonly EnrichmentService? _enrichment;
    private readonly EidetConfig? _config;
    private readonly string _baseUrl;
    private readonly DateTime _startedAt;

    public MetaEndpoints(MemoryService svc, EnrichmentService? enrichment, EidetConfig? config,
        string baseUrl, DateTime startedAt)
    {
        _svc = svc;
        _enrichment = enrichment;
        _config = config;
        _baseUrl = baseUrl;
        _startedAt = startedAt;
    }

    public Task Health(HttpListenerContext ctx)
        => HttpJson.WriteAsync(ctx, new { status = "ok", version = EidetVersion.Current });

    public async Task Status(HttpListenerContext ctx, CancellationToken ct)
    {
        var info = await _svc.GetStoreInfoAsync(ct);

        // Check Ollama health if enrichment is configured
        object? ollamaStatus = null;
        if (_config?.Enrichment.OllamaEnabled == true && _enrichment is not null)
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
        else if (_config is not null)
        {
            ollamaStatus = new { enabled = false };
        }

        await HttpJson.WriteAsync(ctx, new
        {
            version = EidetVersion.Current,
            status = "running",
            uptime = (DateTime.UtcNow - _startedAt).ToString(@"d\.hh\:mm\:ss"),
            api = _baseUrl,
            database = info,
            ollama = ollamaStatus,
        });
    }

    public async Task Root(HttpListenerContext ctx)
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

        await HttpJson.WriteAsync(ctx, new
        {
            service = "Eidet Memory Service",
            version = EidetVersion.Current,
            endpoints = new
            {
                ui = "/ui",
                health = "/api/health",
                status = "/api/status",
                docs = "https://github.com/stevehansen/eidet",
            },
        });
    }
}
