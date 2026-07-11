using System.Text.RegularExpressions;
using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

/// <summary>
/// Pack round-trip for the functional-stage dimension: a staged memory emits <c>stage=</c> in its
/// per-memory comment (omitted when None, mirroring the valence/provenance treatment) and survives
/// export→import so the subtask category isn't lost when shared as a pack.
/// </summary>
public class MarkdownPackFormatStageTests
{
    private static MemoryEntry Entry(MemoryType type, string content, FunctionalStage stage) => new()
    {
        Type = type,
        Content = content,
        Stage = stage,
        Importance = 0.7f,
        Confidence = 0.7f,
        Provenance = MemoryProvenance.AgentInferred,
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void Serialize_emits_stage_only_for_staged_memories()
    {
        var pack = new EidetPack
        {
            Id = "t", Name = "T", Version = "1.0.0", Author = "test",
            Entries =
            [
                Entry(MemoryType.Procedure, "Run the failing test in isolation before touching the fix", FunctionalStage.Test),
                Entry(MemoryType.Insight, "The recall cache is a generation-token map keyed by repo scope", FunctionalStage.None),
            ]
        };

        var md = MarkdownPackFormat.Serialize(pack);

        Assert.Contains("stage=test", md);
        Assert.Single(Regex.Matches(md, "stage="));
    }

    [Fact]
    public void RoundTrip_preserves_stage_and_leaves_none_none()
    {
        var pack = new EidetPack
        {
            Id = "t", Name = "T", Version = "1.0.0", Author = "test",
            Entries =
            [
                Entry(MemoryType.Procedure, "Run the failing test in isolation before touching the fix", FunctionalStage.Test),
                Entry(MemoryType.Insight, "The recall cache is a generation-token map keyed by repo scope", FunctionalStage.None),
            ]
        };

        var restored = MarkdownPackFormat.Deserialize(MarkdownPackFormat.Serialize(pack));

        Assert.Equal(FunctionalStage.Test, restored.Entries.Single(e => e.Type == MemoryType.Procedure).Stage);
        Assert.Equal(FunctionalStage.None, restored.Entries.Single(e => e.Type == MemoryType.Insight).Stage);
    }
}
