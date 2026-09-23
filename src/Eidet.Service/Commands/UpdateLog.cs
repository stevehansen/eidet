using System.Text.RegularExpressions;
using Eidet.Core.Configuration;
using Eidet.Core.Update;

namespace Eidet.Service.Commands;

/// <summary>
/// Reads back what the detached Windows update script wrote. That script runs after
/// <c>eidet update</c> has already exited 0, so <c>update.log</c> is the only place its outcome
/// lands; this is what lets <c>eidet status</c> say an update failed instead of just "update
/// available" (#97).
/// </summary>
internal static partial class UpdateLog
{
    public static string DefaultPath => Path.Combine(ConfigManager.GetConfigDir(), "update.log");

    /// <summary>
    /// The last outcome line, when it is a failure whose target version is still newer than
    /// <paramref name="currentVersion"/>. Null when the log is missing or unreadable, the last
    /// outcome was a success, or the failed version has since been installed another way.
    /// </summary>
    public static string? LastUnresolvedFailure(string currentVersion, string? path = null)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path ?? DefaultPath); }
        catch { return null; }

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (SuccessLine().IsMatch(line)) return null;

            var failure = FailureLine().Match(line);
            if (failure.Success)
                return SemanticVersion.IsNewer(currentVersion, failure.Groups["to"].Value) ? line : null;
        }
        return null;
    }

    [GeneratedRegex(@"- Updated from v\S+ to v\S+")]
    private static partial Regex SuccessLine();

    [GeneratedRegex(@"- Update from v\S+ to v(?<to>\S+) (FAILED|could not be verified)")]
    private static partial Regex FailureLine();
}
