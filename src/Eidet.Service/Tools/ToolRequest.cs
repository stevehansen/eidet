using System.Text.Json;

namespace Eidet.Service.Tools;

/// <summary>
/// One transport-agnostic tool invocation. The dispatcher wraps every call into this shape;
/// handlers never see HTTP or JSON-RPC plumbing.
/// </summary>
public readonly record struct ToolRequest(
    string Tool,
    string RepoId,
    JsonElement Arguments,
    string Source,
    CancellationToken Ct);
