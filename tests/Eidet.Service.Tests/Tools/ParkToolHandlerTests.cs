using System.Text.Json;
using Eidet.Core.LooseEnds;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

public class ParkToolHandlerTests
{
    [Fact]
    public async Task Park_ValidNote_ReturnsOkWithOpenState()
    {
        var handler = NewHandler(out var store);

        var result = await Invoke(handler, new
        {
            note = "flaky integration test in the auth path, revisit the retry logic",
            tags = new[] { "auth", "testing" },
            priority = 1,
        });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal(1, result.ResultCount);
        Assert.Contains("Parked:", result.HumanSummary);

        // Payload carries { id, state = "open" }; tags + priority landed on the stored end.
        var stored = Assert.Single(store.All);
        Assert.Equal(LooseEndState.Open, stored.State);
        Assert.Equal(1, stored.Priority);
        Assert.Equal(["auth", "testing"], stored.Tags);
    }

    [Fact]
    public async Task Park_DefaultPriority_IsNormal()
    {
        var handler = NewHandler(out var store);

        var result = await Invoke(handler, new { note = "review the cache eviction policy on cold start" });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal(2, store.All.Single().Priority);
    }

    [Fact]
    public async Task Park_MissingNote_ThrowsMissingArgument()
    {
        var handler = NewHandler(out _);

        await Assert.ThrowsAsync<MissingToolArgumentException>(async () =>
            await Invoke(handler, new { tags = new[] { "x" } }));
    }

    [Fact]
    public async Task Park_SecretNote_ReturnsRejected()
    {
        var handler = NewHandler(out var store);

        var result = await Invoke(handler, new { note = "deploy key AKIAIOSFODNN7EXAMPLE for the bucket" });

        Assert.Equal(ToolStatus.Rejected, result.Status);
        Assert.Empty(store.All);
    }

    private static ParkToolHandler NewHandler(out FakeLooseEndStore store)
    {
        store = new FakeLooseEndStore();
        var svc = new LooseEndService(store, new FakePromotionPort(), TimeProvider.System);
        return new ParkToolHandler(svc);
    }

    private static Task<ToolResult> Invoke(ParkToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_park",
            "test-repo",
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));
}
