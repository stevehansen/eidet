using Eidet.Core.Enrichment;

namespace Eidet.Core.Tests.Enrichment;

/// <summary>
/// Settles the chain contract: first backend that is up and answers wins, a down or empty
/// backend hands the request on, and <c>ModelName</c> names whoever actually answered.
/// </summary>
public class FallbackEnrichmentAdapterTests
{
    private static readonly EnrichmentRequest Request = new(EnrichmentPrompt.OneLiner, "content");

    private sealed class NamedAdapter(string model) : IEnrichmentPort
    {
        public bool IsAvailable { get; set; } = true;
        public string? Answer { get; set; } = model + "-answer";
        public int Calls { get; private set; }
        public string? ModelName => model;

        public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(IsAvailable);

        public Task<string?> CompleteAsync(EnrichmentRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(IsAvailable ? Answer : null);
        }
    }

    [Fact]
    public async Task PrimaryUp_AnswersFromPrimary_NeverTouchesFallback()
    {
        var primary = new NamedAdapter("deepseek");
        var fallback = new NamedAdapter("gemma4");
        var chain = new FallbackEnrichmentAdapter([primary, fallback]);

        Assert.Equal("deepseek-answer", await chain.CompleteAsync(Request));
        Assert.Equal(0, fallback.Calls);
        Assert.Equal("deepseek", chain.ModelName);
    }

    [Fact]
    public async Task PrimaryDown_FallsThrough_AndModelNameFollows()
    {
        var primary = new NamedAdapter("deepseek") { IsAvailable = false };
        var fallback = new NamedAdapter("gemma4");
        var chain = new FallbackEnrichmentAdapter([primary, fallback]);

        Assert.Equal("deepseek", chain.ModelName); // primary's until something answers
        Assert.Equal("gemma4-answer", await chain.CompleteAsync(Request));
        Assert.Equal(0, primary.Calls); // health gate spared the completion call
        Assert.Equal("gemma4", chain.ModelName);
    }

    [Fact]
    public async Task PrimaryUpButEmpty_FallsThrough()
    {
        var primary = new NamedAdapter("deepseek") { Answer = null };
        var fallback = new NamedAdapter("gemma4");
        var chain = new FallbackEnrichmentAdapter([primary, fallback]);

        Assert.Equal("gemma4-answer", await chain.CompleteAsync(Request));
        Assert.Equal(1, primary.Calls);
    }

    [Fact]
    public async Task AllDown_IsUnavailable_AndReturnsNull()
    {
        var chain = new FallbackEnrichmentAdapter([
            new NamedAdapter("deepseek") { IsAvailable = false },
            new NamedAdapter("gemma4") { IsAvailable = false },
        ]);

        Assert.False(chain.IsAvailable);
        Assert.False(await chain.CheckHealthAsync());
        Assert.Null(await chain.CompleteAsync(Request));
    }

    [Fact]
    public async Task AnyUp_IsAvailable()
    {
        var chain = new FallbackEnrichmentAdapter([
            new NamedAdapter("deepseek") { IsAvailable = false },
            new NamedAdapter("gemma4"),
        ]);

        Assert.True(chain.IsAvailable);
        Assert.True(await chain.CheckHealthAsync());
    }

    [Fact]
    public async Task PrimaryRecovers_TakesOverAgain()
    {
        var primary = new NamedAdapter("deepseek") { IsAvailable = false };
        var fallback = new NamedAdapter("gemma4");
        var chain = new FallbackEnrichmentAdapter([primary, fallback]);

        await chain.CompleteAsync(Request);
        Assert.Equal("gemma4", chain.ModelName);

        primary.IsAvailable = true;
        Assert.Equal("deepseek-answer", await chain.CompleteAsync(Request));
        Assert.Equal("deepseek", chain.ModelName);
    }

    [Fact]
    public void EmptyChain_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new FallbackEnrichmentAdapter([]));
    }
}
