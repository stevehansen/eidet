using Eidet.Core.Update;

namespace Eidet.Core.Tests.Update;

public class UpdateCheckerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eidet-update-" + Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_dir, "update-check.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string IndexJson = """
        { "versions": ["0.9.0", "0.10.0", "0.11.0-rc.1", "0.10.2", "not-a-version"] }
        """;

    private static string LeafJson(DateTimeOffset published) =>
        $$"""{ "published": "{{published:o}}", "listed": true }""";

    private UpdateChecker CheckerReturning(string? index, string? leaf) =>
        new(fetch: (url, _) => Task.FromResult(url.Contains("flatcontainer") ? index : leaf),
            cachePath: CachePath);

    [Fact]
    public void Picks_the_highest_stable_version_not_the_last_listed()
    {
        // 0.10.2 comes after 0.11.0-rc.1 in the array; the answer must be the highest *stable*
        // one, and the unparseable entry must be skipped rather than winning by being last.
        Assert.Equal("0.10.2", UpdateChecker.SelectLatestStable(IndexJson));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{ "versions": [] }""")]
    [InlineData("""{ "versions": ["nope"] }""")]
    public void Answers_nothing_rather_than_throwing_on_a_bad_index(string json)
    {
        Assert.Null(UpdateChecker.SelectLatestStable(json));
    }

    [Fact]
    public void Reads_the_publish_date_from_a_registration_leaf()
    {
        var when = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(when, UpdateChecker.ReadPublishedDate(LeafJson(when)));
    }

    [Fact]
    public void Reads_the_publish_date_from_a_nested_catalog_entry()
    {
        var when = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var json = $$"""{ "catalogEntry": { "published": "{{when:o}}" } }""";
        Assert.Equal(when, UpdateChecker.ReadPublishedDate(json));
    }

    [Fact]
    public void A_leaf_without_a_date_yields_no_date_rather_than_a_default()
    {
        // Must not fall back to DateTimeOffset.MinValue — that would read as "ancient" and sail
        // straight through the age gate.
        Assert.Null(UpdateChecker.ReadPublishedDate("""{ "listed": true }"""));
    }

    [Fact]
    public async Task Writes_a_cache_the_notice_surfaces_can_read()
    {
        var published = DateTimeOffset.UtcNow.AddDays(-3);
        var status = await CheckerReturning(IndexJson, LeafJson(published)).CheckAsync("0.10.0");

        Assert.NotNull(status);
        Assert.Equal("0.10.2", status!.Latest);
        Assert.True(status.UpdateAvailable);

        var cached = UpdateChecker.ReadCache(CachePath);
        Assert.NotNull(cached);
        Assert.Equal("0.10.2", cached!.Latest);
        Assert.Equal(published, cached.LatestPublishedAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task An_unreachable_nuget_leaves_the_previous_cache_alone()
    {
        var good = CheckerReturning(IndexJson, LeafJson(DateTimeOffset.UtcNow.AddDays(-3)));
        await good.CheckAsync("0.10.0");

        var offline = CheckerReturning(null, null);
        Assert.Null(await offline.CheckAsync("0.10.0"));

        // The stale answer is better than no answer — a night without connectivity should not
        // erase what we already knew.
        Assert.Equal("0.10.2", UpdateChecker.ReadCache(CachePath)?.Latest);
    }

    [Fact]
    public async Task A_missing_publish_date_still_reports_the_version()
    {
        var status = await CheckerReturning(IndexJson, leaf: null).CheckAsync("0.10.0");

        Assert.Equal("0.10.2", status!.Latest);
        Assert.Null(status.LatestPublishedAt);
        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    public void A_missing_cache_reads_as_nothing_to_say()
    {
        Assert.Null(UpdateChecker.ReadCache(Path.Combine(_dir, "does-not-exist.json")));
    }

    [Fact]
    public void A_corrupt_cache_reads_as_nothing_to_say()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath, "{ this is not json");
        Assert.Null(UpdateChecker.ReadCache(CachePath));
    }
}
