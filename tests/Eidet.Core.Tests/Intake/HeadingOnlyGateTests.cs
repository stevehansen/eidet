using Eidet.Core.Domain;
using Eidet.Core.Intake;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Intake;

/// <summary>
/// The authority on what counts as a body-less section, and on intake refusing to store one.
///
/// <see cref="MarkdownIntake.MinSectionLength"/> cannot express this rule: it measures length, and
/// "## Development Patterns" is 23 characters of pure heading. A field corpus accumulated 1,000
/// heading-only memories that way, 843 of which carried an LLM-invented one-liner that then rendered
/// into wake-ups ahead of the summary honestly reporting there was no content.
///
/// The risk in the other direction is over-rejection, so the "keeps" cases below are as load-bearing
/// as the "rejects" ones: a heading plus one command is a real memory.
/// </summary>
public class HeadingOnlyGateTests
{
    [Theory]
    [InlineData("## Architecture")]
    [InlineData("# steve")]
    [InlineData("## Development Patterns")]            // 23 chars: passes the length gate
    [InlineData("### Docker\n```bash")]                 // heading + a bare fence delimiter
    [InlineData("## Quick Links\n\n\n")]
    [InlineData("# A\n## B\n### C")]                    // nothing but headings, all the way down
    [InlineData("## Commands\n```")]
    [InlineData("")]
    [InlineData("   \n\t\n")]
    public void Rejects_sections_with_no_body(string content) =>
        Assert.True(MarkdownIntake.IsHeadingOnly(content), $"expected heading-only: <{content}>");

    [Theory]
    [InlineData("## Build\ndotnet build")]
    [InlineData("### Docker\n```bash\ndocker compose up\n```")]
    [InlineData("The scheduler uses RavenDB Refresh as its alarm clock.")]
    [InlineData("## Notes\n- one\n- two")]
    [InlineData("#### Deeper heading counts as body, because the splitter never sections on it")]
    public void Keeps_sections_that_have_any_body_at_all(string content) =>
        Assert.False(MarkdownIntake.IsHeadingOnly(content), $"expected a body: <{content}>");

    /// <summary>
    /// One character of body is enough. The gate exists to reject emptiness, not terseness — enrichment
    /// consults the same predicate, so a floor here would strand real terse memories with no summary.
    /// Measuring how much body there is belongs to <see cref="MarkdownIntake.MinSectionLength"/>.
    /// </summary>
    [Theory]
    [InlineData("## Run\nx=1")]
    [InlineData("x")]
    public void A_very_short_body_is_still_a_body(string content) =>
        Assert.False(MarkdownIntake.IsHeadingOnly(content));

    [Fact]
    public void Punctuation_alone_under_a_heading_is_not_a_body() =>
        Assert.True(MarkdownIntake.IsHeadingOnly("## Divider\n-"));

    // ─── The gate, as intake applies it ───────────────────────────────────

    [Fact]
    public async Task Intake_skips_a_heading_only_candidate_and_keeps_the_rest_of_the_batch()
    {
        var store = new InMemoryEidetStore();
        var service = new IntakeService(
            store,
            // Both headings clear MinSectionLength on their own — that is the whole point: length cannot
            // express this rule, so a gate that only measured length would store both.
            [new StubExtractor(
                new IntakeMemory("CLAUDE.md", MemoryType.Insight, "## Architecture Overview", [], 0.5f),
                new IntakeMemory("CLAUDE.md", MemoryType.Insight, "## Development Patterns", [], 0.5f),
                new IntakeMemory("CLAUDE.md", MemoryType.Insight, "## Build\nRun dotnet build before tests.", [], 0.5f))],
            new MemoryService(store));

        var result = await service.IngestAsync("repo-a", "/x");

        Assert.Equal(1, result.NewCount);
        var kept = Assert.Single(await store.BrowseAsync("repo-a", 0, 10));
        Assert.Contains("dotnet build", kept.Content);
        Assert.Equal(2, result.Items.Count(i => i.SkipReason == "heading with no body"));
    }

    /// <summary>
    /// The skip reason must name the real cause. "too short" would send anyone reading the intake
    /// report looking for a length to raise, and raising it is exactly the wrong fix.
    /// </summary>
    [Fact]
    public async Task Skip_reason_distinguishes_body_less_from_too_short()
    {
        var store = new InMemoryEidetStore();
        var service = new IntakeService(
            store,
            [new StubExtractor(
                new IntakeMemory("CLAUDE.md", MemoryType.Insight, "## Development Patterns", [], 0.5f),
                new IntakeMemory("CLAUDE.md", MemoryType.Insight, "tiny", [], 0.5f))],
            new MemoryService(store));

        var result = await service.IngestAsync("repo-a", "/x");

        Assert.Equal(0, result.NewCount);
        Assert.Contains(result.Items, i => i.SkipReason == "heading with no body");
        Assert.Contains(result.Items, i => i.SkipReason == "too short");
    }

    private sealed class StubExtractor(params IntakeMemory[] candidates) : IIntakeExtractor
    {
        public string Name => "test.stub";

        public bool AppliesTo(IntakeContext ctx) => true;

        public async Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct)
        {
            foreach (var candidate in candidates)
                await sink.AddMemoryAsync(candidate, ct);
        }
    }
}
