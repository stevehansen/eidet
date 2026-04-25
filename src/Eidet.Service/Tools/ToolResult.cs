namespace Eidet.Service.Tools;

/// <summary>
/// Transport-agnostic tool outcome. <see cref="Payload"/> is the structured body REST renders as
/// JSON on success. <see cref="HumanSummary"/> is the one-shot text MCP / CLI render. The two
/// slots are orthogonal so a handler cannot accidentally satisfy one transport while breaking
/// the other.
/// </summary>
public sealed record ToolResult(
    ToolStatus Status,
    object? Payload,
    string? HumanSummary,
    int? ResultCount = null,
    string? DuplicateId = null)
{
    public bool IsOk => Status == ToolStatus.Ok;

    public static ToolResult Ok(object? payload, string summary, int? count = null) =>
        new(ToolStatus.Ok, payload, summary, count);

    public static ToolResult NotFound(string message) =>
        new(ToolStatus.NotFound, null, message);

    public static ToolResult BadRequest(string message) =>
        new(ToolStatus.BadRequest, null, message);

    public static ToolResult Conflict(string message, string? duplicateId = null) =>
        new(ToolStatus.Conflict, null, message, DuplicateId: duplicateId);

    public static ToolResult Rejected(string message) =>
        new(ToolStatus.Rejected, null, message);

    public static ToolResult Internal(string message) =>
        new(ToolStatus.Internal, null, message);
}
