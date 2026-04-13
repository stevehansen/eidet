using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class EnrichmentServiceTests
{
    [Fact]
    public void NullEnrichmentService_IsNotAvailable()
    {
        var svc = NullEnrichmentService.Instance;
        Assert.False(svc.IsAvailable);
    }

    [Fact]
    public async Task NullEnrichmentService_GenerateOneLiner_ReturnsNull()
    {
        var result = await NullEnrichmentService.Instance.GenerateOneLinerAsync("some content");
        Assert.Null(result);
    }

    [Fact]
    public async Task NullEnrichmentService_GenerateSummary_ReturnsNull()
    {
        var result = await NullEnrichmentService.Instance.GenerateSummaryAsync("some content");
        Assert.Null(result);
    }

    [Fact]
    public async Task NullEnrichmentService_GenerateForesightHint_ReturnsNull()
    {
        var result = await NullEnrichmentService.Instance.GenerateForesightHintAsync("some content");
        Assert.Null(result);
    }

    [Fact]
    public async Task NullEnrichmentService_ExtractEntities_ReturnsEmpty()
    {
        var result = await NullEnrichmentService.Instance.ExtractEntitiesAsync("some content");
        Assert.Empty(result);
    }

    [Fact]
    public async Task NullEnrichmentService_MergeObservations_ReturnsNull()
    {
        var result = await NullEnrichmentService.Instance.MergeObservationsAsync(["obs1", "obs2"]);
        Assert.Null(result);
    }

    [Fact]
    public async Task NullEnrichmentService_DetectConflict_ReturnsNull()
    {
        var result = await NullEnrichmentService.Instance.DetectConflictAsync("new", "existing");
        Assert.Null(result);
    }

    [Fact]
    public async Task NullEnrichmentService_CheckHealth_ReturnsFalse()
    {
        var result = await NullEnrichmentService.Instance.CheckHealthAsync();
        Assert.False(result);
    }

    [Fact]
    public void NullEnrichmentService_Singleton_SameInstance()
    {
        Assert.Same(NullEnrichmentService.Instance, NullEnrichmentService.Instance);
    }

    [Fact]
    public void OllamaEnrichmentService_InitialState_IsAvailable()
    {
        // Before first health check, IsAvailable returns true (to trigger lazy check)
        using var svc = new OllamaEnrichmentService("http://localhost:11434", "gemma4");
        Assert.True(svc.IsAvailable);
    }

    [Fact]
    public async Task OllamaEnrichmentService_CheckHealth_Unavailable_ReturnsFalse()
    {
        // Connect to a port that's almost certainly not listening
        using var svc = new OllamaEnrichmentService("http://localhost:19999", "gemma4");
        var result = await svc.CheckHealthAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task OllamaEnrichmentService_ChatFailsGracefully()
    {
        // When health check fails, all generate methods return null
        using var svc = new OllamaEnrichmentService("http://localhost:19999", "gemma4");
        var oneLiner = await svc.GenerateOneLinerAsync("test content");
        Assert.Null(oneLiner);
    }

    // ─── StripChainOfThought tests ──────────────────────────────────────

    [Fact]
    public void StripChainOfThought_Null_ReturnsNull()
    {
        Assert.Null(OllamaEnrichmentService.StripChainOfThought(null));
    }

    [Fact]
    public void StripChainOfThought_Whitespace_ReturnsNull()
    {
        // Whitespace-only input has no meaningful content to extract
        var result = OllamaEnrichmentService.StripChainOfThought("  ");
        Assert.Null(result);
    }

    [Fact]
    public void StripChainOfThought_CleanText_PassesThrough()
    {
        var text = "Cross-repo search defaults to false, preventing accidental result leakage.";
        Assert.Equal(text, OllamaEnrichmentService.StripChainOfThought(text));
    }

    [Fact]
    public void StripChainOfThought_ChannelMarker_ExtractsAnswer()
    {
        var input = "The user wants an ultra-compact summary.\nDrafting options:\n1. First option\n<channel|>Cross-repo search defaults to false.";
        Assert.Equal("Cross-repo search defaults to false.", OllamaEnrichmentService.StripChainOfThought(input));
    }

    [Fact]
    public void StripChainOfThought_MultipleChannelMarkers_TakesLastThenTrims()
    {
        var input = "Reasoning...<channel|>First attempt<channel|>Final answer here.";
        // Takes after LAST <channel|> in the first pass, then trims any inner ones
        Assert.Equal("Final answer here.", OllamaEnrichmentService.StripChainOfThought(input));
    }

    [Fact]
    public void StripChainOfThought_ThinkTags_StripsThinking()
    {
        var input = "<think>Let me analyze this...\nThe key change is...</think>The actual summary here.";
        Assert.Equal("The actual summary here.", OllamaEnrichmentService.StripChainOfThought(input));
    }

    [Fact]
    public void StripChainOfThought_ChannelMarkerWithRepeatedAnswer()
    {
        // Takes after the last <channel|>, then strips trailing <channel|> segments
        var input = "Reasoning<channel|>The answer.<channel|>The answer repeated.";
        // Last <channel|> gives "The answer repeated." — no further markers
        Assert.Equal("The answer repeated.", OllamaEnrichmentService.StripChainOfThought(input));
    }

    [Fact]
    public void StripChainOfThought_RealCorruptedSummary()
    {
        var input = "The user wants me to summarize a technical memory change.\nConstraint Checklist:\n1. Yes.\n2. Yes.\n<channel|>The Memories_Search index now uses a composite SearchText field for richer searching.";
        Assert.Equal("The Memories_Search index now uses a composite SearchText field for richer searching.",
            OllamaEnrichmentService.StripChainOfThought(input));
    }

    [Fact]
    public void StripChainOfThought_RealCorruptedOneLiner()
    {
        var input = "The user wants an ultra-compact, 10-word maximum summary.\nDraft 1: option A\nDraft 2: option B\n<channel|>Use AndAlso() in RavenDB queries to enforce AND logic over OR.";
        Assert.Equal("Use AndAlso() in RavenDB queries to enforce AND logic over OR.",
            OllamaEnrichmentService.StripChainOfThought(input));
    }
}
