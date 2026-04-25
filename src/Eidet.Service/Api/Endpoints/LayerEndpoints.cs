using System.Net;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// REST endpoints for the layer subsystem (mount/list/unmount/sync). All four
/// short-circuit to 501 when their underlying services are not configured, so
/// the service still runs in single-repo mode without layer support wired in.
/// </summary>
internal sealed class LayerEndpoints
{
    private readonly LayerService? _layers;
    private readonly LayerSyncService? _layerSync;

    public LayerEndpoints(LayerService? layers, LayerSyncService? layerSync)
    {
        _layers = layers;
        _layerSync = layerSync;
    }

    public async Task GetLayers(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_layers is null) { await HttpJson.WriteAsync(ctx, new { error = "Layer service not available" }, 501); return; }
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo)) { await HttpJson.WriteAsync(ctx, new { error = "Missing 'repo' parameter" }, 400); return; }
        var layers = await _layers.GetApplicableLayersAsync(RepoIdNormalizer.Normalize(repo), ct: ct);
        await HttpJson.WriteAsync(ctx, new { repo, layers });
    }

    public async Task MountLayer(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_layers is null) { await HttpJson.WriteAsync(ctx, new { error = "Layer service not available" }, 501); return; }
        var req = await HttpJson.ReadAsync<MountLayerRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.LayerId) || string.IsNullOrEmpty(req.Name))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required fields: layerId, name" }, 400);
            return;
        }
        var layer = await _layers.MountAsync(req.LayerId, req.Name, req.Type,
            req.ApplicableRepos, req.ApplicablePackages, req.SourcePath, ct: ct);
        await HttpJson.WriteAsync(ctx, layer, 201);
    }

    public async Task LayerSync(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_layerSync is null) { await HttpJson.WriteAsync(ctx, new { error = "Layer sync service not available" }, 501); return; }
        var req = await HttpJson.ReadAsync<LayerSyncRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Path))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required field: path" }, 400);
            return;
        }

        if (req.Preview == true)
        {
            var preview = await _layerSync.PreviewAsync(req.Path, req.LayerId, ct);
            await HttpJson.WriteAsync(ctx, preview);
        }
        else
        {
            var result = await _layerSync.SyncAsync(req.Path, req.LayerId, req.RemoveStale ?? true, ct);
            await HttpJson.WriteAsync(ctx, result);
        }
    }

    public async Task UnmountLayer(HttpListenerContext ctx, string layerId, CancellationToken ct)
    {
        if (_layers is null) { await HttpJson.WriteAsync(ctx, new { error = "Layer service not available" }, 501); return; }
        var decoded = Uri.UnescapeDataString(layerId);
        var ok = await _layers.UnmountAsync(decoded, ct);
        if (ok) await HttpJson.WriteAsync(ctx, new { unmounted = true });
        else await HttpJson.WriteAsync(ctx, new { error = "Layer not found" }, 404);
    }
}
