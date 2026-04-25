using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eidet.Service.Tools.Formatters;

/// <summary>
/// Renders a <see cref="ToolResult"/> over an HTTP listener response. Maps statuses to HTTP codes
/// and writes <see cref="ToolResult.Payload"/> for Ok or an <c>{ error, duplicateId? }</c> object
/// for non-Ok statuses.
/// </summary>
public static class RestFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static int StatusCodeFor(ToolStatus status, int successStatus = 200) => status switch
    {
        ToolStatus.Ok => successStatus,
        ToolStatus.NotFound => 404,
        ToolStatus.BadRequest => 400,
        ToolStatus.Conflict => 409,
        ToolStatus.Rejected => 422,
        ToolStatus.Internal => 500,
        _ => 500,
    };

    public static async Task WriteAsync(HttpListenerContext ctx, ToolResult result, int successStatus = 200)
    {
        var statusCode = StatusCodeFor(result.Status, successStatus);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json";

        object body = result.IsOk
            ? result.Payload ?? new { }
            : new { error = result.HumanSummary, duplicateId = result.DuplicateId };

        await JsonSerializer.SerializeAsync(ctx.Response.OutputStream, body, JsonOptions);
        ctx.Response.Close();
    }
}
