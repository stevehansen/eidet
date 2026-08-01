using Eidet.Core.Domain;
using Eidet.Core.Memory;

namespace Eidet.Core.Tests.Services;

public class MemoryServiceTests
{
    [Fact]
    public void ComputeL1Score_HighImportanceScoresHigher()
    {
        var now = DateTime.UtcNow;
        var high = MakeEntry(importance: 1.0f, confidence: 0.7f, accessCount: 0, createdAt: now);
        var low = MakeEntry(importance: 0.1f, confidence: 0.7f, accessCount: 0, createdAt: now);

        Assert.True(RecallScoring.ComputeL1Score(high, now) > RecallScoring.ComputeL1Score(low, now));
    }

    [Fact]
    public void RecallCache_Invalidate_ignores_null_or_empty_scope()
    {
        // A malformed/legacy entry with no RepoId must not crash a bulk/background caller:
        // Invalidate funnels into a ConcurrentDictionary that rejects null keys, so null/empty
        // scopes are dropped rather than thrown on.
        var cache = new RecallCache();

        var ex = Record.Exception(() =>
        {
            cache.Invalidate(null!);
            cache.Invalidate("");
            cache.InvalidateAll([null!, "", "repo-a"]);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void ComputeL1Score_RecentScoresHigher()
    {
        var now = DateTime.UtcNow;
        var recent = MakeEntry(importance: 0.5f, confidence: 0.7f, accessCount: 0, createdAt: now);
        var old = MakeEntry(importance: 0.5f, confidence: 0.7f, accessCount: 0, createdAt: now.AddDays(-30));

        Assert.True(RecallScoring.ComputeL1Score(recent, now) > RecallScoring.ComputeL1Score(old, now));
    }

    [Fact]
    public void ComputeL1Score_FrequentAccessScoresHigher()
    {
        var now = DateTime.UtcNow;
        var frequentlyAccessed = MakeEntry(importance: 0.5f, confidence: 0.7f, accessCount: 10, createdAt: now);
        var neverAccessed = MakeEntry(importance: 0.5f, confidence: 0.7f, accessCount: 0, createdAt: now);

        Assert.True(RecallScoring.ComputeL1Score(frequentlyAccessed, now) > RecallScoring.ComputeL1Score(neverAccessed, now));
    }

    [Fact]
    public void ComputeL1Score_SevenDayHalfLife()
    {
        var now = DateTime.UtcNow;
        var atHalfLife = MakeEntry(importance: 0.0f, confidence: 0.0f, accessCount: 0, createdAt: now.AddDays(-7));
        var fresh = MakeEntry(importance: 0.0f, confidence: 0.0f, accessCount: 0, createdAt: now);

        // At half-life, recency should be ~50% of fresh recency
        // Score = 0*0.3 + 0*0.15 + recency*0.25 + 0*0.3 = recency*0.25
        var ratio = RecallScoring.ComputeL1Score(atHalfLife, now) / RecallScoring.ComputeL1Score(fresh, now);
        Assert.InRange(ratio, 0.45, 0.55); // ~50%
    }

    [Fact]
    public void ResolveProvenance_MapsCorrectly()
    {
        Assert.Equal(MemoryProvenance.UserStated, ProvenanceResolver.FromSource("user"));
        Assert.Equal(MemoryProvenance.AgentInferred, ProvenanceResolver.FromSource("claude-session"));
        Assert.Equal(MemoryProvenance.Consolidation, ProvenanceResolver.FromSource("consolidation"));
        Assert.Equal(MemoryProvenance.Intake, ProvenanceResolver.FromSource("intake"));
        Assert.Equal(MemoryProvenance.Pack, ProvenanceResolver.FromSource("pack"));
        Assert.Equal(MemoryProvenance.Pack, ProvenanceResolver.FromSource("bundle")); // legacy alias
        Assert.Equal(MemoryProvenance.System, ProvenanceResolver.FromSource("system"));
        // An unrecognized source no longer resolves to a trusted origin (#80).
        Assert.Equal(MemoryProvenance.Unknown, ProvenanceResolver.FromSource("unknown"));
    }

    [Fact]
    public void EstimateTokens_FourCharsPerToken()
    {
        Assert.Equal(1, RecallScoring.EstimateTokens(4));
        Assert.Equal(1, RecallScoring.EstimateTokens(1));
        Assert.Equal(25, RecallScoring.EstimateTokens(100));
        Assert.Equal(30, RecallScoring.EstimateTokens(120));
    }

    private static MemoryEntry MakeEntry(
        float importance, float confidence, int accessCount, DateTime createdAt) => new()
    {
        Importance = importance,
        Confidence = confidence,
        AccessCount = accessCount,
        CreatedAt = createdAt,
    };
}
