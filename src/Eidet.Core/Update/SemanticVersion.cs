using System.Globalization;

namespace Eidet.Core.Update;

/// <summary>
/// Just enough SemVer to answer "is this candidate strictly newer than what I am running?".
///
/// Exists because the update path used to compare version strings for equality, which made any
/// version that merely *differed* from NuGet's latest look like an available update — including a
/// locally built or pre-release binary that is actually ahead. Under a manual `eidet update` that is
/// a confusing prompt; under an unattended nightly it is a silent downgrade.
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<SemanticVersion>
{
    public bool IsPreRelease => PreRelease is not null;

    /// <summary>
    /// Parses <c>MAJOR.MINOR.PATCH[-prerelease][+build]</c>. Build metadata is discarded — SemVer
    /// says it carries no ordering. Anything that does not fit that shape fails rather than being
    /// coerced, so an unreadable version can never be mistaken for an old one.
    /// </summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.Trim();
        if (span.StartsWith('v') || span.StartsWith('V'))
            span = span[1..];

        var plus = span.IndexOf('+');
        if (plus >= 0) span = span[..plus];

        string? pre = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            pre = span[(dash + 1)..];
            span = span[..dash];
            if (pre.Length == 0) return false;
        }

        var parts = span.Split('.');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;

        version = new SemanticVersion(major, minor, patch, pre);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var byNumber = Major.CompareTo(other.Major);
        if (byNumber != 0) return byNumber;
        byNumber = Minor.CompareTo(other.Minor);
        if (byNumber != 0) return byNumber;
        byNumber = Patch.CompareTo(other.Patch);
        if (byNumber != 0) return byNumber;

        // A pre-release sorts below its own release: 0.11.0-rc1 < 0.11.0.
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    /// <summary>
    /// True only when <paramref name="candidate"/> is unambiguously newer than
    /// <paramref name="current"/>. Either side failing to parse answers false: a version we cannot
    /// read is not a version we offer to install.
    /// </summary>
    public static bool IsNewer(string? current, string? candidate)
    {
        if (!TryParse(current, out var a)) return false;
        if (!TryParse(candidate, out var b)) return false;
        return b.CompareTo(a) > 0;
    }
}
