using System.Net;
using Eidet.Core.Enrichment;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// On-demand Ollama enrichment for the Web UI: dispatches
/// <c>oneliner</c>/<c>summary</c>/<c>foresight</c>/<c>entities</c>
/// against the configured <see cref="EnrichmentService"/>. 503s when the
/// service is disabled or the underlying port reports unavailable.
/// </summary>
internal sealed class EnrichEndpoint
{
    private readonly EnrichmentService? _enrichment;

    public EnrichEndpoint(EnrichmentService? enrichment) => _enrichment = enrichment;

    public async Task Enrich(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_enrichment is null || !_enrichment.IsAvailable)
        {
            await HttpJson.WriteAsync(ctx, new { error = "Enrichment service not available. Configure Ollama in eidet setup." }, 503);
            return;
        }

        var req = await HttpJson.ReadAsync<EnrichRequest>(ctx);
        if (req is null || string.IsNullOrEmpty(req.Content) || string.IsNullOrEmpty(req.Task))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required fields: content, task" }, 400);
            return;
        }

        try
        {
            string? result = req.Task switch
            {
                "oneliner" => await _enrichment.GenerateAsync(EnrichmentPrompt.OneLiner, req.Content, ct),
                "summary" => await _enrichment.GenerateAsync(EnrichmentPrompt.Summary, req.Content, ct),
                "foresight" => await _enrichment.GenerateAsync(EnrichmentPrompt.ForesightHint, req.Content, ct),
                "entities" => string.Join(", ", await _enrichment.ExtractEntitiesAsync(req.Content, ct)),
                _ => null,
            };

            if (result is null)
                await HttpJson.WriteAsync(ctx, new { error = $"Unknown task: {req.Task}. Use: oneliner, summary, foresight, entities" }, 400);
            else
                await HttpJson.WriteAsync(ctx, new { task = req.Task, result });
        }
        catch (Exception ex)
        {
            await HttpJson.WriteAsync(ctx, new { error = $"Enrichment failed: {ex.Message}" }, 500);
        }
    }
}
