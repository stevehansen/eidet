using Eidet.Core.Domain;
using Eidet.Core.Intake;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Intake;

/// <summary>
/// The orchestrator sink runs the always-on write gate per candidate, for EVERY intake source —
/// skip-not-abort. Closes the historical bypass where the whole pipeline ran under
/// <c>BulkOptions.Validate=false</c> and stored unscanned content (issue #63).
/// </summary>
public class IntakeSinkValidationTests
{
    private sealed class StubExtractor(params IntakeMemory[] candidates) : IIntakeExtractor
    {
        public string Name => "test.stub";

        public bool AppliesTo(IntakeContext ctx) => true;

        public async Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct)
        {
            foreach (var candidate in candidates)
                await sink.AddMemoryAsync(candidate, ct);
        }
    }

    [Fact]
    public async Task SecretCandidate_SkippedNotAborted_ForFileIntakeToo()
    {
        var extractor = new StubExtractor(
            new IntakeMemory("secrets.md", MemoryType.Insight,
                "Deployment note: use key AKIAIOSFODNN7EXAMPLE for the S3 bucket.", [], 0.5f),
            new IntakeMemory("notes.md", MemoryType.Insight,
                "The scheduler uses RavenDB Refresh as its alarm clock.", [], 0.5f));
        var store = new InMemoryEidetStore();
        var service = new IntakeService(store, [extractor], new MemoryService(store));

        var result = await service.IngestAsync("test-repo", "/x");

        Assert.Equal(1, result.NewCount);
        Assert.Equal(1, result.SkippedCount);

        var skipped = Assert.Single(result.Items, i => i.WasSkipped);
        Assert.Equal("secrets.md", skipped.Source);
        Assert.StartsWith("secret-scan:", skipped.SkipReason);
        Assert.Equal("", skipped.Content); // redacted — the caught secret must not leak via the result

        var entry = Assert.Single(await store.BrowseAsync("test-repo", 0, 10));
        Assert.Contains("alarm clock", entry.Content);
    }

    [Fact]
    public async Task LowSignalCandidate_SkippedWithSignalReason()
    {
        // Exactly 20 chars: passes the sink's length check but matches the signal gate's
        // low-signal pattern list ("file does not exist" + trailing period).
        var extractor = new StubExtractor(
            new IntakeMemory("notes.md", MemoryType.Insight, "file does not exist.", [], 0.5f));
        var store = new InMemoryEidetStore();
        var service = new IntakeService(store, [extractor], new MemoryService(store));

        var result = await service.IngestAsync("test-repo", "/x");

        Assert.Equal(0, result.NewCount);
        var skipped = Assert.Single(result.Items);
        Assert.StartsWith("signal:", skipped.SkipReason);
        Assert.Empty(await store.BrowseAsync("test-repo", 0, 10));
    }
}
