namespace Eidet.Core.Update;

/// <summary>How this copy of Eidet was installed, which decides whether it can replace itself.</summary>
public enum InstallFlavor
{
    /// <summary>Installed by `dotnet tool install -g eidet`. The only flavor that can self-update.</summary>
    DotnetTool,

    /// <summary>Running inside a container — the image is the unit of update, not the binary.</summary>
    Container,

    /// <summary>A standalone binary from GitHub Releases, or an unrecognised layout.</summary>
    Standalone,
}

public static class InstallFlavorDetector
{
    /// <summary>
    /// Best-effort classification of the running binary. Deliberately biased towards
    /// <see cref="InstallFlavor.Standalone"/>: guessing "not a dotnet tool" costs a notice-only
    /// night, while guessing wrong the other way ends in a failed `dotnet tool update` at 04:00.
    /// </summary>
    public static InstallFlavor Detect(string? baseDirectory = null)
    {
        if (IsContainer()) return InstallFlavor.Container;

        var dir = baseDirectory ?? AppContext.BaseDirectory;
        return IsDotnetToolPath(dir) ? InstallFlavor.DotnetTool : InstallFlavor.Standalone;
    }

    private static bool IsContainer() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true",
            StringComparison.OrdinalIgnoreCase)
        || File.Exists("/.dockerenv");

    /// <summary>
    /// A global tool runs out of the tool store (<c>~/.dotnet/tools/.store/eidet/…</c>); a
    /// <c>--tool-path</c> install runs out of a <c>.store</c> alongside the shim. Both carry the
    /// package id inside a <c>.store</c> segment, which nothing else does.
    /// </summary>
    internal static bool IsDotnetToolPath(string directory)
    {
        var normalized = directory.Replace('\\', '/');
        return normalized.Contains("/.store/eidet/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.dotnet/tools/", StringComparison.OrdinalIgnoreCase);
    }
}
