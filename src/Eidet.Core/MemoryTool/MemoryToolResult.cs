namespace Eidet.Core.MemoryTool;

/// <summary><see cref="Text"/> is returned to Claude verbatim; <see cref="IsError"/> maps to the <c>tool_result</c> <c>is_error</c> flag.</summary>
public sealed record MemoryToolResult(bool IsError, string Text);
