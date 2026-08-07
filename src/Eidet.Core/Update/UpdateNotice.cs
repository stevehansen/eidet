using Eidet.Core.Configuration;

namespace Eidet.Core.Update;

/// <summary>
/// The "a new version is out" line, rationed to once per process.
///
/// Every surface — CLI, MCP, TUI — can call <see cref="TryTake"/> unconditionally without
/// knowing about the others: the first caller with something to say gets the message, everyone
/// after gets null. Reads the cache written by the nightly check and never the network, so the
/// cost of asking is a file read even on the MCP hot path.
/// </summary>
public static class UpdateNotice
{
    private static int _taken;

    /// <summary>
    /// Whether the user wants to hear about updates at all, resolved once per process. Read here
    /// rather than threaded through every caller so that a surface can stay ignorant of config,
    /// and parsed once so the MCP path never re-reads config.json to decide to say nothing.
    /// </summary>
    private static readonly Lazy<bool> CheckEnabled = new(() =>
    {
        try { return ConfigManager.Load().Update.Check; }
        catch { return true; }
    });

    /// <summary>
    /// The message, or null when there is nothing to say, checking is disabled, or this process
    /// has already shown it. Note that "nothing to say" does not consume the ration — a long-lived
    /// service that learns about a release at 04:00 still gets to mention it afterwards.
    /// </summary>
    public static string? TryTake(bool? enabled = null, string? cachePath = null)
    {
        if (!(enabled ?? CheckEnabled.Value)) return null;
        if (Volatile.Read(ref _taken) != 0) return null;

        var status = UpdateChecker.ReadCache(cachePath);

        // Compare against the running binary rather than the version recorded in the cache: the
        // cache outlives the update it announced, and re-announcing an installed version is worse
        // than staying quiet.
        if (!SemanticVersion.IsNewer(EidetVersion.Current, status?.Latest)) return null;

        if (Interlocked.Exchange(ref _taken, 1) != 0) return null;

        return $"Eidet {status!.Latest} is available (running {EidetVersion.Current}) — run: eidet update";
    }

    internal static void Reset() => Volatile.Write(ref _taken, 0);
}
