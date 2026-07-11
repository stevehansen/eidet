using System.Net;
using System.Text.Json;
using Eidet.Core.MemoryTool;

namespace Eidet.Service.Api.Endpoints;

/// <summary>
/// Claude memory-tool command endpoint for any-language Claude apps:
/// <c>POST /api/eidet/memory-tool?repo=...</c> accepts the raw <c>memory_20250818</c>
/// tool_use input envelope and relays the translator's result verbatim as
/// <c>{ isError, text }</c> — <c>is_error</c> belongs to the tool protocol, so the HTTP
/// status is 200 either way. Auth/scopes are enforced upstream by <c>ApiAuthGate</c>.
/// </summary>
internal sealed class MemoryToolEndpoint
{
    private readonly IMemoryFileStore _files;

    public MemoryToolEndpoint(IMemoryFileStore files) => _files = files;

    public async Task Handle(HttpListenerContext ctx, CancellationToken ct)
    {
        var repo = ctx.Request.QueryString["repo"];
        if (string.IsNullOrEmpty(repo))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Missing required query param: repo" }, 400);
            return;
        }

        JsonElement envelope;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            envelope = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync(ct));
        }
        catch (JsonException)
        {
            await HttpJson.WriteAsync(ctx, new { error = "Invalid JSON body" }, 400);
            return;
        }

        var translator = new MemoryToolTranslator(_files, repo);
        var result = await translator.ExecuteAsync(MemoryCommand.Parse(envelope), ct);
        await HttpJson.WriteAsync(ctx, new { isError = result.IsError, text = result.Text });
    }
}
