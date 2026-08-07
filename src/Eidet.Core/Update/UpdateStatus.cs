namespace Eidet.Core.Update;

/// <summary>
/// The result of the last look at NuGet, as cached on disk. Every display surface reads this
/// record rather than the network, so a CLI invocation or an MCP session start never pays a
/// round-trip to decide whether to mention an update.
/// </summary>
public sealed record UpdateStatus
{
    /// <summary>The version that was running when the check was made.</summary>
    public required string Current { get; init; }

    /// <summary>Newest stable version on NuGet, or null when the check could not resolve one.</summary>
    public string? Latest { get; init; }

    /// <summary>
    /// When <see cref="Latest"/> was published. Null when NuGet answered with the version list but
    /// not the date — which deliberately blocks unattended installs (see <see cref="IsInstallable"/>).
    /// </summary>
    public DateTimeOffset? LatestPublishedAt { get; init; }

    public required DateTimeOffset CheckedAt { get; init; }

    public bool UpdateAvailable => SemanticVersion.IsNewer(Current, Latest);

    /// <summary>
    /// Whether an unattended install should proceed. Stricter than <see cref="UpdateAvailable"/> by
    /// the age gate: because releases are immutable, a bad build cannot be replaced in place, only
    /// superseded — so holding back for <paramref name="minimumAge"/> is the one thing that stops a
    /// broken release reaching every machine before anyone can react. An unknown publish date fails
    /// the gate rather than passing it; the notice still shows, only the automation waits.
    /// </summary>
    public bool IsInstallable(TimeSpan minimumAge, DateTimeOffset now) =>
        UpdateAvailable
        && LatestPublishedAt is { } published
        && now - published >= minimumAge;
}
