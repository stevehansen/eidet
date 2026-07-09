using System.Text.RegularExpressions;
using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

/// <summary>
/// Pack round-trip for the Valence dimension: a signed memory emits <c>valence=</c> in its per-memory
/// comment (omitted when Neutral, mirroring the provenance treatment) and survives export→import so
/// dead-ends don't lose their stance when shared as a pack.
/// </summary>
public class MarkdownPackFormatValenceTests
{
    private static MemoryEntry Entry(MemoryType type, string content, string oneLiner, Valence valence) => new()
    {
        Type = type,
        Content = content,
        OneLiner = oneLiner,
        Valence = valence,
        Importance = 0.7f,
        Confidence = 0.7f,
        Provenance = MemoryProvenance.AgentInferred,
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void Serialize_emits_valence_only_for_signed_memories()
    {
        var pack = new EidetPack
        {
            Id = "test", Name = "Test", Version = "1.0.0", Author = "test",
            Entries =
            [
                Entry(MemoryType.Heuristic,
                    "Do NOT batch store writes via BulkInsert inside the mutation ctx — it corrupts recall-cache invalidation",
                    "BulkInsert in mutation ctx corrupts recall cache", Valence.Refuting),
                Entry(MemoryType.Insight,
                    "The recall cache is an in-memory generation-token map keyed by repo scope",
                    "Recall cache is a generation-token map", Valence.Neutral),
            ]
        };

        var md = MarkdownPackFormat.Serialize(pack);

        // The refuting memory carries the stance; the neutral one omits it entirely.
        Assert.Contains("valence=refuting", md);
        Assert.Single(Regex.Matches(md, "valence="));
    }

    [Fact]
    public void RoundTrip_preserves_refuting_valence_and_leaves_neutral_neutral()
    {
        var pack = new EidetPack
        {
            Id = "dead-ends", Name = "Dead Ends", Version = "1.0.0", Author = "test",
            Entries =
            [
                Entry(MemoryType.Heuristic,
                    "Do NOT batch store writes via BulkInsert inside the mutation ctx — it corrupts recall-cache invalidation",
                    "BulkInsert in mutation ctx corrupts recall cache", Valence.Refuting),
                Entry(MemoryType.Insight,
                    "The recall cache is an in-memory generation-token map keyed by repo scope",
                    "Recall cache is a generation-token map", Valence.Neutral),
            ]
        };

        var restored = MarkdownPackFormat.Deserialize(MarkdownPackFormat.Serialize(pack));

        var refuting = restored.Entries.Single(e => e.Type == MemoryType.Heuristic);
        var neutral = restored.Entries.Single(e => e.Type == MemoryType.Insight);
        Assert.Equal(Valence.Refuting, refuting.Valence);
        Assert.Equal(Valence.Neutral, neutral.Valence);
    }
}
