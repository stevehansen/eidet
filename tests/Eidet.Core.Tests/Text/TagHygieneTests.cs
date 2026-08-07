using Eidet.Core.Text;

namespace Eidet.Core.Tests.Text;

public class TagHygieneTests
{
    [Theory]
    [InlineData("the")]
    [InlineData("to")]
    [InlineData("and")]
    [InlineData("with")]
    [InlineData("2026")]   // date fragments mined out of headings
    [InlineData("04")]
    [InlineData("35")]     // issue numbers
    [InlineData("s")]      // possessive fragment
    public void Drops_tokens_that_cannot_narrow_a_recall(string tag) =>
        Assert.True(TagHygiene.IsNoise(tag));

    [Theory]
    [InlineData("ravendb")]
    [InlineData("cache-coherence")]
    [InlineData("rfc-22")]   // contains digits but identifies a subject
    [InlineData("h2")]
    [InlineData("todo")]     // load-bearing here; must survive
    [InlineData("notes")]
    public void Keeps_tokens_that_identify_a_subject(string tag) =>
        Assert.False(TagHygiene.IsNoise(tag));

    [Fact]
    public void Caps_growth_so_a_reconsolidated_union_cannot_cover_the_corpus()
    {
        var sprawl = Enumerable.Range(0, 200).Select(i => $"tag-{i}");

        var cleaned = TagHygiene.Clean(sprawl);

        Assert.Equal(TagHygiene.MaxTags, cleaned.Count);
    }

    [Fact]
    public void Cap_sheds_the_vaguest_tags_first()
    {
        // Multi-word tags are more specific than bare words, so they must survive the cap.
        var tags = new[] { "a1", "b2", "c3", "d4", "cache-coherence", "e5", "f6" };

        var cleaned = TagHygiene.Clean(tags, max: 3);

        Assert.Contains("cache-coherence", cleaned);
    }

    [Fact]
    public void Is_idempotent_so_it_can_run_at_mine_consolidate_and_write_time()
    {
        var once = TagHygiene.Clean(["The", "RavenDB", "2026", "cache-coherence", "ravendb"]);
        var twice = TagHygiene.Clean(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Deduplicates_case_insensitively()
    {
        var cleaned = TagHygiene.Clean(["RavenDB", "ravendb", "RAVENDB"]);

        Assert.Single(cleaned);
        Assert.Equal("ravendb", cleaned[0]);
    }

    [Fact]
    public void Null_yields_empty_rather_than_throwing() =>
        Assert.Empty(TagHygiene.Clean(null));
}
