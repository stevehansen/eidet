namespace Eidet.Service.Update;

/// <summary>
/// Decides whether the CLI may print the update notice for a given invocation.
///
/// Mostly a list of ways an extra line of stdout does damage. The one that matters is
/// <c>eidet mcp</c>: stdout there is the JSON-RPC channel to the client, so a friendly message is
/// a protocol violation that shows up as a broken MCP server rather than as a stray line.
/// </summary>
public static class UpdateNoticeGate
{
    public static bool ShouldStayQuiet(IReadOnlyList<string> args)
    {
        if (Console.IsOutputRedirected) return true;

        if (args.Count == 0) return false;

        // `eidet mcp` speaks JSON-RPC over stdout.
        if (args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase)) return true;

        // The update command is already a conversation about versions.
        if (args[0].Equals("update", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var arg in args)
            if (arg.Equals("--json", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
