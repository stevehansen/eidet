using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class MaintenanceServiceTests
{
    [Fact]
    public void ComputeWordSimilarity_IdenticalStrings_ReturnsOne()
    {
        var score = MaintenanceService.ComputeWordSimilarity(
            "RavenDB uses Corax engine for search",
            "RavenDB uses Corax engine for search");
        Assert.Equal(1.0f, score);
    }

    [Fact]
    public void ComputeWordSimilarity_CompletelyDifferent_ReturnsZeroish()
    {
        var score = MaintenanceService.ComputeWordSimilarity(
            "alpha beta gamma delta",
            "one two three four");
        Assert.Equal(0.0f, score);
    }

    [Fact]
    public void ComputeWordSimilarity_HighOverlap_AboveThreshold()
    {
        var score = MaintenanceService.ComputeWordSimilarity(
            "RavenDB uses Corax engine for full-text search queries",
            "RavenDB uses Corax engine for full-text search indexing");
        Assert.True(score > 0.7f, $"Score {score} should be > 0.7 for high-overlap strings");
    }

    [Fact]
    public void ComputeWordSimilarity_BothEmpty_ReturnsOne()
    {
        Assert.Equal(1.0f, MaintenanceService.ComputeWordSimilarity("", ""));
    }

    [Fact]
    public void ComputeWordSimilarity_OneEmpty_ReturnsZero()
    {
        Assert.Equal(0.0f, MaintenanceService.ComputeWordSimilarity("hello world", ""));
        Assert.Equal(0.0f, MaintenanceService.ComputeWordSimilarity("", "hello world"));
    }

    [Fact]
    public void ComputeWordSimilarity_CaseInsensitive()
    {
        var score = MaintenanceService.ComputeWordSimilarity(
            "RavenDB Corax Engine",
            "ravendb corax engine");
        Assert.Equal(1.0f, score);
    }

    [Fact]
    public void ComputeWordSimilarity_PunctuationIgnored()
    {
        var score = MaintenanceService.ComputeWordSimilarity(
            "Hello, world! How are you?",
            "Hello world How are you");
        Assert.Equal(1.0f, score);
    }

    [Fact]
    public void ComputeWordSimilarity_PartialOverlap_MidRange()
    {
        // "alpha beta gamma" vs "alpha beta delta" → intersection={alpha,beta}=2, union={alpha,beta,gamma,delta}=4 → 0.5
        var score = MaintenanceService.ComputeWordSimilarity(
            "alpha beta gamma",
            "alpha beta delta");
        Assert.InRange(score, 0.45f, 0.55f);
    }

    [Fact]
    public void ComputeWordSimilarity_DuplicateCandidate_AboveJaccard085()
    {
        // Near-duplicates that should be caught by dedup
        var score = MaintenanceService.ComputeWordSimilarity(
            "The RavenDB index uses Corax search engine with vector similarity and full-text search combined into a single query",
            "The RavenDB index uses Corax search engine with vector similarity and full-text search combined into one query");
        Assert.True(score >= 0.85f, $"Score {score} should be >= 0.85 for near-duplicate content");
    }
}
