using Eidet.Core.Update;

namespace Eidet.Core.Tests.Update;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("0.10.0", 0, 10, 0, null)]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v0.11.0", 0, 11, 0, null)]
    [InlineData("0.11.0-rc.1", 0, 11, 0, "rc.1")]
    [InlineData("0.11.0+build.7", 0, 11, 0, null)]
    [InlineData("0.11.0-rc.1+build.7", 0, 11, 0, "rc.1")]
    public void Parses_the_shapes_NuGet_publishes(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(new SemanticVersion(major, minor, patch, pre), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("not-a-version")]
    [InlineData("1.2.3-")]
    public void Refuses_what_it_cannot_read(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("0.10.0", "0.10.1")]
    [InlineData("0.10.0", "0.11.0")]
    [InlineData("0.10.0", "1.0.0")]
    [InlineData("0.11.0-rc.1", "0.11.0")]
    public void Recognises_a_newer_candidate(string current, string candidate)
    {
        Assert.True(SemanticVersion.IsNewer(current, candidate));
    }

    [Theory]
    [InlineData("0.10.0", "0.10.0")]
    [InlineData("0.11.0", "0.10.0")]
    [InlineData("1.0.0", "0.99.99")]
    [InlineData("0.11.0", "0.11.0-rc.1")]
    public void Rejects_a_candidate_that_is_not_ahead(string current, string candidate)
    {
        Assert.False(SemanticVersion.IsNewer(current, candidate));
    }

    [Fact]
    public void A_locally_built_version_is_never_downgraded()
    {
        // Regression: the update path compared version strings for equality, so a dev build that
        // merely *differed* from NuGet's latest read as out of date. Unattended, that is a
        // silent downgrade at 04:00.
        Assert.False(SemanticVersion.IsNewer("0.11.0-repair", "0.10.0"));
        Assert.NotEqual("0.11.0-repair", "0.10.0");
    }

    [Theory]
    [InlineData(null, "0.10.0")]
    [InlineData("0.10.0", null)]
    [InlineData("garbage", "0.10.0")]
    [InlineData("0.10.0", "garbage")]
    public void An_unreadable_version_is_never_an_update(string? current, string? candidate)
    {
        Assert.False(SemanticVersion.IsNewer(current, candidate));
    }

    [Fact]
    public void Orders_a_prerelease_below_its_own_release()
    {
        var rc = new SemanticVersion(0, 11, 0, "rc.1");
        var release = new SemanticVersion(0, 11, 0, null);
        Assert.True(rc.CompareTo(release) < 0);
        Assert.True(release.CompareTo(rc) > 0);
    }

    [Fact]
    public void Round_trips_through_ToString()
    {
        Assert.Equal("0.11.0", new SemanticVersion(0, 11, 0, null).ToString());
        Assert.Equal("0.11.0-rc.1", new SemanticVersion(0, 11, 0, "rc.1").ToString());
    }
}
