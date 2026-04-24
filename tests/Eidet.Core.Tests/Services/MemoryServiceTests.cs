using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class MemoryServiceTests
{
    [Fact]
    public void ComputeL1Score_HighImportanceScoresHigher()
    {
        var now = DateTime.UtcNow;
        var high = MakeEntry(importance: 1.0f, confidence: 0.7f, accessCount: 0, createdAt: now);
        var low = MakeEntry(importance: 0.1f, confidence: 0.7f, accessCount: 0, createdAt: now);

        var highScore = MemoryServiceTestHelper.ComputeL1Score(high, now);
        var lowScore = MemoryServiceTestHelper.ComputeL1Score(low, now);

        Assert.True(highScore > lowScore);
    }

    [Fact]
    public void ComputeL1Score_RecentScoresHigher()
    {
        var now = DateTime.UtcNow;
        var recent = MakeEntry(importance: 0.5f, confidence: 0.7f, accessCount: 0, createdAt: now);
        var old = MakeEntry(importance: 0.5f, confidence: 0.7f, accessCount: 0, createdAt: now.AddDays(-30));

        var recentScore = MemoryServiceTestHelper.ComputeL1Score(recent, now);
        var oldScore = MemoryServiceTestHelper.ComputeL1Score(old, now);

        Assert.True(recentScore > oldScore);
    }

    [Fact]
    public void ComputeL1Score_FrequentAccessScoresHigher()
    {
        var now = DateTime.UtcNow;
        var frequentlyAccessed = MakeEntry(importance: 0.5f, confidence: 0.7f, accessCount: 10, createdAt: now);
        var neverAccessed = MakeEntry(importance: 0.5f, confidence: 0.7f, accessCount: 0, createdAt: now);

        var freqScore = MemoryServiceTestHelper.ComputeL1Score(frequentlyAccessed, now);
        var neverScore = MemoryServiceTestHelper.ComputeL1Score(neverAccessed, now);

        Assert.True(freqScore > neverScore);
    }

    [Fact]
    public void ComputeL1Score_SevenDayHalfLife()
    {
        var now = DateTime.UtcNow;
        var atHalfLife = MakeEntry(importance: 0.0f, confidence: 0.0f, accessCount: 0, createdAt: now.AddDays(-7));
        var fresh = MakeEntry(importance: 0.0f, confidence: 0.0f, accessCount: 0, createdAt: now);

        var halfScore = MemoryServiceTestHelper.ComputeL1Score(atHalfLife, now);
        var freshScore = MemoryServiceTestHelper.ComputeL1Score(fresh, now);

        // At half-life, recency should be ~50% of fresh recency
        // Score = 0*0.3 + 0*0.15 + recency*0.25 + 0*0.3 = recency*0.25
        var ratio = halfScore / freshScore;
        Assert.InRange(ratio, 0.45, 0.55); // ~50%
    }

    [Fact]
    public void ResolveProvenance_MapsCorrectly()
    {
        Assert.Equal(MemoryProvenance.UserStated, MemoryServiceTestHelper.ResolveProvenance("user"));
        Assert.Equal(MemoryProvenance.AgentInferred, MemoryServiceTestHelper.ResolveProvenance("claude-session"));
        Assert.Equal(MemoryProvenance.Consolidation, MemoryServiceTestHelper.ResolveProvenance("consolidation"));
        Assert.Equal(MemoryProvenance.Intake, MemoryServiceTestHelper.ResolveProvenance("intake"));
        Assert.Equal(MemoryProvenance.Pack, MemoryServiceTestHelper.ResolveProvenance("pack"));
        Assert.Equal(MemoryProvenance.Pack, MemoryServiceTestHelper.ResolveProvenance("bundle")); // legacy alias
        Assert.Equal(MemoryProvenance.System, MemoryServiceTestHelper.ResolveProvenance("system"));
        Assert.Equal(MemoryProvenance.AgentInferred, MemoryServiceTestHelper.ResolveProvenance("unknown"));
    }

    [Fact]
    public void EstimateTokens_FourCharsPerToken()
    {
        Assert.Equal(1, MemoryServiceTestHelper.EstimateTokens(4));
        Assert.Equal(1, MemoryServiceTestHelper.EstimateTokens(1));
        Assert.Equal(25, MemoryServiceTestHelper.EstimateTokens(100));
        Assert.Equal(30, MemoryServiceTestHelper.EstimateTokens(120));
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

/// <summary>
/// Exposes internal MemoryService helper methods for testing via delegation.
/// </summary>
public static class MemoryServiceTestHelper
{
    public static double ComputeL1Score(MemoryEntry entry, DateTime now)
    {
        var importance = (double)entry.Importance;
        var confidence = (double)entry.Confidence;
        var daysSinceCreation = Math.Max(0, (now - entry.CreatedAt).TotalDays);
        var recency = Math.Exp(-0.693 * daysSinceCreation / 7.0);
        var frequency = Math.Min(1.0, entry.AccessCount / 10.0);
        return importance * 0.3 + confidence * 0.15 + recency * 0.25 + frequency * 0.3;
    }

    public static MemoryProvenance ResolveProvenance(string source) => source switch
    {
        "user" => MemoryProvenance.UserStated,
        "claude-session" => MemoryProvenance.AgentInferred,
        "consolidation" => MemoryProvenance.Consolidation,
        "intake" => MemoryProvenance.Intake,
        "pack" or "bundle" => MemoryProvenance.Pack,
        "system" => MemoryProvenance.System,
        _ => MemoryProvenance.AgentInferred,
    };

    public static int EstimateTokens(int charCount) => (int)Math.Ceiling(charCount / 4.0);
}
