using Eidet.Core.Text;

namespace Eidet.Core.Tests.Text;

/// <summary>
/// The authority on what survives the entity field. Entities are exact-match retrieval keys — the
/// index analyzes them with KeywordAnalyzer and cue-anchor expansion looks query terms up against
/// them — so a prose fragment there can never be matched and only dilutes.
///
/// Both directions are load-bearing. The "drops" cases are drawn from a real corpus: a reasoning
/// model answering the extraction prompt with its own chain of thought (443 such strings across 223
/// memories) plus markdown structure left over from summarizing docs. The "keeps" cases are the
/// entities the field exists for, and over-filtering them costs recall silently.
/// </summary>
public class EntityHygieneTests
{
    [Theory]
    // Chain-of-thought leakage, verbatim from the corpus that exposed it.
    [InlineData("The user wants me to act as an information extractor based on a specific set of entity types")]
    [InlineData("Scanning the text \"## 2. Acceptance Criteria\":")]
    [InlineData("Since none of the specified entities are found, I must return an empty string")]
    [InlineData("1. Project names")]
    [InlineData("2) Package names")]
    [InlineData("<channel|>")]
    [InlineData("<|channel|>assistant")]
    // Markdown structure rather than a name.
    [InlineData("## Development Patterns")]
    [InlineData("# CLAUDE.md")]
    [InlineData("```json")]
    [InlineData("```")]
    // Degenerate.
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("x")]
    [InlineData(":")]
    public void Drops_structure_and_commentary(string entity) =>
        Assert.True(EntityHygiene.IsNoise(entity), $"expected noise: <{entity}>");

    [Theory]
    [InlineData("Vidyano.RavenDB")]
    [InlineData("/api/eidet/context")]
    [InlineData("CLAUDE.md")]
    [InlineData("IntakeService")]
    [InlineData("CorpusRepairStage.FoldLineageDuplicatesAsync")]
    [InlineData("src/Eidet.Core/Maintenance/Stages/CorpusRepairStage.cs")]
    [InlineData("404")]                                     // a bare number CAN be an error code
    [InlineData("net10.0")]
    [InlineData("bge-micro-v2")]
    [InlineData("C:\\Program Files\\Microsoft Visual Studio\\MSBuild")]
    [InlineData("eidet_recall")]
    public void Keeps_identifiers(string entity) =>
        Assert.False(EntityHygiene.IsNoise(entity), $"expected an identifier: <{entity}>");

    /// <summary>
    /// A digit run alone must not read as a list marker: it takes the marker punctuation AND a space.
    /// Error codes and versions are entities, and dropping them was the obvious way to get this wrong.
    /// </summary>
    [Fact]
    public void Numbered_list_detection_needs_the_marker_not_just_digits()
    {
        Assert.True(EntityHygiene.IsNoise("3. Retry the request"));
        Assert.False(EntityHygiene.IsNoise("3.14159"));
        Assert.False(EntityHygiene.IsNoise("500"));
        Assert.False(EntityHygiene.IsNoise("7.2.4"));
    }

    [Fact]
    public void Trailing_sentence_punctuation_is_trimmed_not_a_reason_to_drop()
    {
        Assert.Equal("logging", EntityHygiene.Normalize("logging:"));
        Assert.Equal("IntakeService", EntityHygiene.Normalize("IntakeService."));
        // Interior dots are load-bearing and must survive.
        Assert.Equal("Vidyano.Core", EntityHygiene.Normalize("Vidyano.Core"));
        Assert.Equal("CLAUDE.md", EntityHygiene.Normalize(" CLAUDE.md "));
    }

    [Fact]
    public void Clean_dedupes_case_insensitively_and_preserves_first_casing_and_order()
    {
        var cleaned = EntityHygiene.Clean([
            "IntakeService", "## Heading", "intakeservice", "/api/health", "1. Project names", "IntakeService.",
        ]);

        Assert.Equal(["IntakeService", "/api/health"], cleaned);
    }

    /// <summary>
    /// Idempotence is what lets the same rule run at extraction time and again as corpus repair —
    /// without it, the repair stage would rewrite (and re-report) the same entries every night.
    /// </summary>
    [Fact]
    public void Clean_is_idempotent()
    {
        string[] raw = ["Vidyano.RavenDB", "The user wants me to extract entities from this text", "logging:", "```bash"];

        var once = EntityHygiene.Clean(raw);
        var twice = EntityHygiene.Clean(once);

        Assert.Equal(once, twice);
        Assert.Equal(["Vidyano.RavenDB", "logging"], once);
    }

    [Fact]
    public void Clean_of_null_is_empty() => Assert.Empty(EntityHygiene.Clean(null));

    /// <summary>
    /// A run-on is not a name. Guards the mashed-together strings the corpus showed at 146-150 chars
    /// ("/GetNodeInfo/GetDatabases/GetLicenseStatus/..."), which match no cue a query could carry.
    /// </summary>
    [Fact]
    public void Drops_run_on_strings_past_the_length_ceiling() =>
        Assert.True(EntityHygiene.IsNoise(new string('a', 121)));
}
