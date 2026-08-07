using Eidet.Core.Update;

namespace Eidet.Core.Tests.Update;

public class UpdateStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 4, 0, 0, TimeSpan.Zero);

    private static UpdateStatus Status(string current, string? latest, DateTimeOffset? published) => new()
    {
        Current = current,
        Latest = latest,
        LatestPublishedAt = published,
        CheckedAt = Now,
    };

    [Fact]
    public void A_release_older_than_the_gate_is_installable()
    {
        var status = Status("0.10.0", "0.11.0", Now.AddHours(-25));
        Assert.True(status.UpdateAvailable);
        Assert.True(status.IsInstallable(TimeSpan.FromHours(24), Now));
    }

    [Fact]
    public void A_release_younger_than_the_gate_is_announced_but_not_installed()
    {
        // The whole point of the gate: with immutable releases a bad build can only be superseded,
        // so the fleet has to wait long enough for the successor to exist.
        var status = Status("0.10.0", "0.11.0", Now.AddHours(-1));

        Assert.True(status.UpdateAvailable);
        Assert.False(status.IsInstallable(TimeSpan.FromHours(24), Now));
    }

    [Fact]
    public void An_unknown_publish_date_fails_the_gate()
    {
        // Fail closed. An unknown age is not a young release, but it is also not evidence of an
        // old one, and unattended installs are the wrong place to guess.
        var status = Status("0.10.0", "0.11.0", published: null);

        Assert.True(status.UpdateAvailable);
        Assert.False(status.IsInstallable(TimeSpan.FromHours(24), Now));
    }

    [Fact]
    public void A_zero_gate_installs_immediately()
    {
        var status = Status("0.10.0", "0.11.0", Now);
        Assert.True(status.IsInstallable(TimeSpan.Zero, Now));
    }

    [Fact]
    public void An_older_latest_is_neither_available_nor_installable()
    {
        var status = Status("0.11.0", "0.10.0", Now.AddDays(-30));
        Assert.False(status.UpdateAvailable);
        Assert.False(status.IsInstallable(TimeSpan.FromHours(24), Now));
    }

    [Fact]
    public void No_resolved_latest_is_not_an_update()
    {
        var status = Status("0.10.0", latest: null, published: null);
        Assert.False(status.UpdateAvailable);
    }
}
