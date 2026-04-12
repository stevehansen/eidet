using Eidet.Core.Services;
using Raven.Client.Documents;

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
        // NullEnrichmentService means Ollama is disabled — worker should not start.
        // Passing null! for store proves it's never accessed.
        using var worker = new EnrichmentWorker(null!, NullEnrichmentService.Instance);
        await worker.StartAsync(CancellationToken.None);
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        var worker = new EnrichmentWorker(null!, NullEnrichmentService.Instance);
        worker.Dispose();
    }

    [Fact]
    public async Task Dispose_AfterNullEnrichmentStart_DoesNotThrow()
    {
        var worker = new EnrichmentWorker(null!, NullEnrichmentService.Instance);
        await worker.StartAsync(CancellationToken.None);
        worker.Dispose();
    }
}
