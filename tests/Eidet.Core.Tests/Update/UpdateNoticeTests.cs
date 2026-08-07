using System.Text.Json;
using Eidet.Core;
using Eidet.Core.Update;

namespace Eidet.Core.Tests.Update;

[Collection("UpdateNotice")]
public class UpdateNoticeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eidet-notice-" + Guid.NewGuid().ToString("N"));

    public UpdateNoticeTests() => UpdateNotice.Reset();

    public void Dispose()
    {
        UpdateNotice.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A version guaranteed to be ahead of whatever this build reports.</summary>
    private static string Newer()
    {
        Assert.True(SemanticVersion.TryParse(EidetVersion.Current, out var current));
        return new SemanticVersion(current.Major + 1, 0, 0, null).ToString();
    }

    private string WriteCache(string? latest)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "update-check.json");
        var json = JsonSerializer.Serialize(new
        {
            current = EidetVersion.Current,
            latest,
            checkedAt = DateTimeOffset.UtcNow,
        });
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Mentions_a_newer_version_once_and_then_stays_quiet()
    {
        var path = WriteCache(Newer());

        var first = UpdateNotice.TryTake(enabled: true, cachePath: path);
        Assert.NotNull(first);
        Assert.Contains(Newer(), first);
        Assert.Contains(EidetVersion.Current, first);

        Assert.Null(UpdateNotice.TryTake(enabled: true, cachePath: path));
        Assert.Null(UpdateNotice.TryTake(enabled: true, cachePath: path));
    }

    [Fact]
    public void Having_nothing_to_say_does_not_use_up_the_one_message()
    {
        // A service started before a release exists must still be able to mention it after the
        // 04:00 check writes the cache — so a quiet call cannot burn the latch.
        var stale = WriteCache(EidetVersion.Current);
        Assert.Null(UpdateNotice.TryTake(enabled: true, cachePath: stale));

        var fresh = WriteCache(Newer());
        Assert.NotNull(UpdateNotice.TryTake(enabled: true, cachePath: fresh));
    }

    [Fact]
    public void Says_nothing_when_checking_is_disabled()
    {
        var path = WriteCache(Newer());
        Assert.Null(UpdateNotice.TryTake(enabled: false, cachePath: path));

        // ...and disabling must not have consumed the message either.
        Assert.NotNull(UpdateNotice.TryTake(enabled: true, cachePath: path));
    }

    [Fact]
    public void Ignores_a_cache_that_announces_the_version_already_running()
    {
        // The cache outlives the update it announced. Comparing against the live binary rather
        // than the version recorded at check time is what stops the notice repeating post-update.
        var path = WriteCache(EidetVersion.Current);
        Assert.Null(UpdateNotice.TryTake(enabled: true, cachePath: path));
    }

    [Fact]
    public void Ignores_a_cache_announcing_an_older_version()
    {
        Assert.True(SemanticVersion.TryParse(EidetVersion.Current, out var current));
        var older = new SemanticVersion(current.Major, current.Minor, current.Patch, "rc.1").ToString();

        Assert.Null(UpdateNotice.TryTake(enabled: true, cachePath: WriteCache(older)));
    }

    [Fact]
    public void A_missing_cache_is_silence_not_an_error()
    {
        Assert.Null(UpdateNotice.TryTake(enabled: true, cachePath: Path.Combine(_dir, "absent.json")));
    }
}

/// <summary>
/// <see cref="UpdateNotice"/> rations its message with process-wide state, so its tests cannot
/// interleave with each other.
/// </summary>
[CollectionDefinition("UpdateNotice", DisableParallelization = true)]
public class UpdateNoticeCollection;
