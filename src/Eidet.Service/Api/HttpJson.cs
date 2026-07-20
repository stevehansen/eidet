using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eidet.Service.Api;

/// <summary>
/// JSON I/O over <see cref="HttpListenerContext"/>: shared serializer options,
/// camelCase + null-skipping + enum-as-string. Used by every REST endpoint and
/// by <c>RestFormatter</c> for ToolResult payloads, so changes here apply
/// uniformly to anything the API hands out.
/// </summary>
internal static class HttpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task WriteAsync(HttpListenerContext ctx, object body, int statusCode = 200)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.OutputStream, body, Options);
        ctx.Response.Close();
    }

    public static async Task<T?> ReadAsync<T>(HttpListenerContext ctx) where T : class
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        // A missing body is an omitted payload, not malformed JSON: routes with optional bodies
        // (e.g. canon approve) advertise "req is null" as the no-body case, so make it true —
        // Deserialize would throw on the empty string and surface as a 500.
        if (string.IsNullOrWhiteSpace(body)) return null;
        return JsonSerializer.Deserialize<T>(body, Options);
    }

    public static void AddCorsHeaders(HttpListenerContext ctx)
    {
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
        ctx.Response.Headers.Add("Access-Control-Max-Age", "86400");
    }
}
