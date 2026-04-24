using Eidet.Core.Text;

namespace Eidet.Core.Tests.Text;

public class WordSimilarityTests
{
    [Fact]
    public void IdenticalStrings_ReturnsOne()
    {
        var score = WordSimilarity.Compute(
            "RavenDB uses Corax engine for search",
            "RavenDB uses Corax engine for search");
        Assert.Equal(1.0f, score);
    }

    [Fact]
    public void CompletelyDifferent_ReturnsZero()
    {
        var score = WordSimilarity.Compute(
            "alpha beta gamma delta",
            "one two three four");
        Assert.Equal(0.0f, score);
    }

    [Fact]
    public void HighOverlap_AboveThreshold()
    {
        var score = WordSimilarity.Compute(
            "RavenDB uses Corax engine for full-text search queries",
            "RavenDB uses Corax engine for full-text search indexing");
        Assert.True(score > 0.7f, $"Score {score} should be > 0.7 for high-overlap strings");
    }

    [Fact]
    public void BothEmpty_ReturnsOne() =>
        Assert.Equal(1.0f, WordSimilarity.Compute("", ""));

    [Fact]
    public void OneEmpty_ReturnsZero()
    {
        Assert.Equal(0.0f, WordSimilarity.Compute("hello world", ""));
        Assert.Equal(0.0f, WordSimilarity.Compute("", "hello world"));
    }

    [Fact]
    public void CaseInsensitive()
    {
        var score = WordSimilarity.Compute(
            "RavenDB Corax Engine",
            "ravendb corax engine");
        Assert.Equal(1.0f, score);
    }

    [Fact]
    public void PunctuationIgnored()
    {
        var score = WordSimilarity.Compute(
            "Hello, world! How are you?",
            "Hello world How are you");
        Assert.Equal(1.0f, score);
    }

    [Fact]
    public void PartialOverlap_MidRange()
    {
        // "alpha beta gamma" vs "alpha beta delta" → intersection={alpha,beta}=2, union={alpha,beta,gamma,delta}=4 → 0.5
        var score = WordSimilarity.Compute("alpha beta gamma", "alpha beta delta");
        Assert.InRange(score, 0.45f, 0.55f);
    }

    [Fact]
    public void DuplicateCandidate_AboveJaccard085()
    {
        var score = WordSimilarity.Compute(
            "The RavenDB index uses Corax search engine with vector similarity and full-text search combined into a single query",
            "The RavenDB index uses Corax search engine with vector similarity and full-text search combined into one query");
        Assert.True(score >= 0.85f, $"Score {score} should be >= 0.85 for near-duplicate content");
    }
}
