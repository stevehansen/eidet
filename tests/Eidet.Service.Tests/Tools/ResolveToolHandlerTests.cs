using System.Text.Json;
using Eidet.Core.LooseEnds;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

public class ResolveToolHandlerTests
{
    [Fact]
    public async Task Resolve_ValidKind_ReturnsOkWithResolvedState()
    {
        var handler = NewHandler(out var store);
        var id = await Park(store, "tidy up the migration ordering before the next release");

        var result = await Invoke(handler, new { id, kind = "done" });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Contains("Resolved", result.HumanSummary);

        var stored = await store.GetAsync(id);
        Assert.Equal(LooseEndState.Resolved, stored!.State);
        Assert.Equal(ResolutionKind.Done, stored.Resolution);
    }

    [Fact]
    public async Task Resolve_InvalidKind_ReturnsBadRequest()
    {
        var handler = NewHandler(out var store);
        var id = await Park(store, "audit the auth header parsing for edge cases");

        var result = await Invoke(handler, new { id, kind = "finished" });

        Assert.Equal(ToolStatus.BadRequest, result.Status);
        Assert.Contains("Invalid kind", result.HumanSummary);

        // Nothing was resolved.
        var stored = await store.GetAsync(id);
        Assert.Equal(LooseEndState.Open, stored!.State);
    }

    [Fact]
    public async Task Resolve_MissingKind_ThrowsMissingArgument()
    {
        var handler = NewHandler(out var store);
        var id = await Park(store, "follow up on the cache warm-up regression");

        await Assert.ThrowsAsync<MissingToolArgumentException>(async () =>
            await Invoke(handler, new { id }));
    }

    [Fact]
    public async Task Resolve_MissingKind_ViaDispatcher_IsBadRequest()
    {
        var handler = NewHandler(out var store);
        var id = await Park(store, "follow up on the cache warm-up regression");
        var dispatcher = new ToolDispatcher([handler]);

        var result = await dispatcher.InvokeAsync(new ToolRequest(
            "eidet_resolve", "test-repo",
            JsonSerializer.SerializeToElement(new { id }), "test", CancellationToken.None));

        Assert.Equal(ToolStatus.BadRequest, result.Status);
    }

    [Fact]
    public async Task Resolve_UnknownId_ReturnsNotFound()
    {
        var handler = NewHandler(out _);

        var result = await Invoke(handler, new { id = "looseends/test-repo/deadbeef0000", kind = "done" });

        Assert.Equal(ToolStatus.NotFound, result.Status);
        Assert.Contains("not found", result.HumanSummary, StringComparison.OrdinalIgnoreCase);
    }

    private static ResolveToolHandler NewHandler(out FakeLooseEndStore store)
    {
        store = new FakeLooseEndStore();
        var svc = new LooseEndService(store, new FakePromotionPort(), TimeProvider.System);
        return new ResolveToolHandler(svc);
    }

    private static async Task<string> Park(FakeLooseEndStore store, string note)
    {
        var end = new LooseEnd
        {
            Id = $"looseends/test-repo/{Guid.NewGuid():N}"[..40],
            RepoId = "test-repo",
            Note = note,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.StoreAsync(end);
        return end.Id;
    }

    private static Task<ToolResult> Invoke(ResolveToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_resolve",
            "test-repo",
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));
}
