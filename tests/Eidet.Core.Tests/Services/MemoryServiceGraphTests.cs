using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

/// <summary>
/// Lineage edges in <see cref="MemoryService.GetGraphDataAsync"/> (#80). Before this change a
/// <c>DerivedFrom</c> edge was emitted only when the target happened to be one of the nodes in the graph
/// window, so a citation into nothing rendered identically to no citation at all — the failure looked like
/// success. Now every citation produces an edge, flagged <see cref="GraphEdgeStatus.Missing"/> when the
/// target resolves nowhere.
///
/// The distinction that makes the flag usable: a target that merely falls OUTSIDE the requested window is
/// resolved against the store and stays <see cref="GraphEdgeStatus.Ok"/>. Conflating the two would flag
/// most citations in any corpus larger than the window, which is the false alarm that trains readers to
/// ignore the flag.
/// </summary>
public class MemoryServiceGraphTests
{
    private const string Repo = "graph-repo";

    [Fact]
    public async Task UnresolvableCitation_YieldsAMissingEdge_RatherThanBeingDropped()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var citer = await svc.StoreAsync(new StoreOptions(
            Repo, "an insight derived from an observation that was hard-deleted", MemoryType.Insight)
        {
            DerivedFrom = [$"memories/{Repo}/observation/deadbeef1234"],
        });

        var graph = await svc.GetGraphDataAsync(Repo);

        var edge = Assert.Single(graph.Edges, e => e.Relation == "derived");
        Assert.Equal($"memories/{Repo}/observation/deadbeef1234", edge.From);
        Assert.Equal(citer.Id, edge.To);
        Assert.Equal(GraphEdgeStatus.Missing, edge.Status);
    }

    [Fact]
    public async Task CitationOutsideTheWindow_IsResolved_AndNotFlagged()
    {
        // The target is superseded, so it is absent from the browse window that builds the graph nodes but
        // still resolvable in the store. Same code path an out-of-window target takes: not in the node set,
        // present in the store ⇒ Ok.
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var target = await svc.StoreAsync(Repo, "the original observation about connection pooling limits", MemoryType.Observation);
        await svc.EditAsync(target.Id!, new EditOptions { Content = "a revised observation about connection pooling limits" });

        var citer = await svc.StoreAsync(new StoreOptions(
            Repo, "an insight derived from the connection pooling observation", MemoryType.Insight)
        {
            DerivedFrom = [target.Id!],
        });

        var graph = await svc.GetGraphDataAsync(Repo);

        Assert.DoesNotContain(graph.Nodes, n => n.Id == target.Id);   // outside the window
        var edge = Assert.Single(graph.Edges, e => e.To == citer.Id && e.Relation == "derived");
        Assert.Equal(GraphEdgeStatus.Ok, edge.Status);
    }

    [Fact]
    public async Task CitationInsideTheWindow_IsOk()
    {
        var store = new InMemoryEidetStore();
        var svc = new MemoryService(store);
        var target = await svc.StoreAsync(Repo, "the contributing observation about the cache eviction policy", MemoryType.Observation);
        var citer = await svc.StoreAsync(new StoreOptions(
            Repo, "an insight derived from the cache eviction observation", MemoryType.Insight)
        {
            DerivedFrom = [target.Id!],
        });

        var graph = await svc.GetGraphDataAsync(Repo);

        var edge = Assert.Single(graph.Edges, e => e.Relation == "derived");
        Assert.Equal(target.Id, edge.From);
        Assert.Equal(citer.Id, edge.To);
        Assert.Equal(GraphEdgeStatus.Ok, edge.Status);
    }
}
