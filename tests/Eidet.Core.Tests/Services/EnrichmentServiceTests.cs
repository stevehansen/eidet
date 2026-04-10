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
}
