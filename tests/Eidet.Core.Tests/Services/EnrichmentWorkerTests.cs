using Eidet.Core.Enrichment;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class EnrichmentWorkerTests
{
    [Fact]
    public void SubscriptionName_IsConstant()
    {
        Assert.Equal("enrichment-worker", EnrichmentWorker.SubscriptionName);
    }

    [Fact]
    public async Task StartAsync_WithNullEnrichment_DoesNotTouchStore()
    {
        // CreateNull() means Ollama is disabled — worker should not start.
        // Passing null! for store proves it's never accessed.
        using var enrichment = EnrichmentService.CreateNull();
        using var worker = new EnrichmentWorker(null!, enrichment, null!);
        await worker.StartAsync(CancellationToken.None);
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        using var enrichment = EnrichmentService.CreateNull();
        var worker = new EnrichmentWorker(null!, enrichment, null!);
        worker.Dispose();
    }

    [Fact]
    public async Task Dispose_AfterNullEnrichmentStart_DoesNotThrow()
    {
        using var enrichment = EnrichmentService.CreateNull();
        var worker = new EnrichmentWorker(null!, enrichment, null!);
        await worker.StartAsync(CancellationToken.None);
        worker.Dispose();
    }
}
