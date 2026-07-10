using Eidet.Core.Domain;
using Eidet.Core.Enrichment;

namespace Eidet.Core.Tests.Enrichment;

/// <summary>
/// Parser contract for the Reflector's model reply. Valid JSON arrays yield N proposals; malformed,
/// empty, CoT-wrapped (<c>&lt;think&gt;…&lt;/think&gt;</c>), and code-fenced replies all degrade to
/// <c>[]</c> so a bad reply simply mints nothing that pass. Unknown <c>type</c>/<c>valence</c> strings
/// degrade to the safest option (Observation / Neutral) rather than dropping the proposal.
/// </summary>
public class ReflectionProposalParserTests
{
    [Fact]
    public void Valid_array_yields_all_proposals_with_fields_parsed()
    {
        const string raw =
            """
            [
              {"content":"Prefer server-side alpha learning for recall blend tuning","type":"insight","valence":"affirming","tags":["recall","alpha"]},
              {"content":"Do not reuse a global static HttpClient for Ollama under burst","type":"heuristic","valence":"refuting","tags":["ollama"]}
            ]
            """;

        var proposals = ReflectionProposalParser.Parse(raw);

        Assert.Equal(2, proposals.Count);
        Assert.Equal(MemoryType.Insight, proposals[0].Type);
        Assert.Equal(Valence.Affirming, proposals[0].Valence);
        Assert.Equal(new[] { "recall", "alpha" }, proposals[0].Tags);
        Assert.Equal(MemoryType.Heuristic, proposals[1].Type);
        Assert.Equal(Valence.Refuting, proposals[1].Valence);
    }

    [Fact]
    public void CoT_wrapped_array_is_extracted()
    {
        const string raw =
            "<think>Let me synthesize the durable lesson here.</think>\n" +
            """[{"content":"Warm the connection pool at boot to kill first-request latency","type":"insight","valence":"neutral","tags":[]}]""";

        var proposals = ReflectionProposalParser.Parse(raw);

        var p = Assert.Single(proposals);
        Assert.StartsWith("Warm the connection pool", p.Content);
    }

    [Fact]
    public void Code_fenced_array_is_extracted()
    {
        const string raw =
            "```json\n" +
            """[{"content":"Run migrations before starting the app server on deploy","type":"procedure","valence":"neutral","tags":["deploy"]}]""" +
            "\n```";

        var proposals = ReflectionProposalParser.Parse(raw);

        var p = Assert.Single(proposals);
        Assert.Equal(MemoryType.Procedure, p.Type);
        Assert.Equal("deploy", Assert.Single(p.Tags));
    }

    [Fact]
    public void Unknown_type_and_valence_degrade_to_observation_and_neutral()
    {
        const string raw =
            """[{"content":"Some durable lesson learned from the residue this pass","type":"wat","valence":"sideways","tags":null}]""";

        var proposals = ReflectionProposalParser.Parse(raw);

        var p = Assert.Single(proposals);
        Assert.Equal(MemoryType.Observation, p.Type);
        Assert.Equal(Valence.Neutral, p.Valence);
        Assert.Empty(p.Tags);
    }

    [Fact]
    public void Blank_content_entries_are_dropped()
    {
        const string raw =
            """[{"content":"   ","type":"insight"},{"content":"A genuinely durable insight worth keeping","type":"insight"}]""";

        var proposals = ReflectionProposalParser.Parse(raw);

        var p = Assert.Single(proposals);
        Assert.StartsWith("A genuinely durable", p.Content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all, just prose about reflection")]
    [InlineData("{\"content\":\"an object, not an array\"}")] // object, not the expected array
    [InlineData("[ {\"content\": ")]                            // truncated / unbalanced
    [InlineData("<think>no array follows this chain of thought</think>")]
    public void Malformed_or_empty_replies_yield_empty_list(string? raw)
    {
        Assert.Empty(ReflectionProposalParser.Parse(raw));
    }
}
