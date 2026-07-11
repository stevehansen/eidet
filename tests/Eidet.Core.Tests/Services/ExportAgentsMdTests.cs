using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class ExportAgentsMdTests
{
    private static MemoryEntry Entry(MemoryType type, string content, float importance, string? oneLiner = null) => new()
    {
        Id = $"memories/test-repo/{type.ToString().ToLowerInvariant()}/{Guid.NewGuid():N}",
        RepoId = "test-repo",
        Type = type,
        Content = content,
        Importance = importance,
        OneLiner = oneLiner,
        IsLatest = true,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
    };

    [Fact]
    public async Task RendersAgentsShape_TypedSections_ObservationsExcluded()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Entry(MemoryType.Insight, "The API routes all tools through one dispatcher.", 0.8f, "Single dispatcher for REST+MCP"));
        await store.StoreAsync(Entry(MemoryType.Procedure, "1. Stop service\n2. Update\n3. Restart", 0.7f, "Service update procedure"));
        await store.StoreAsync(Entry(MemoryType.Heuristic, "Prefer embedded RavenDB for local dev setups.", 0.6f));
        await store.StoreAsync(Entry(MemoryType.Observation, "Session residue that stays out of exports.", 0.9f));
        var export = new ExportService(store, new MemoryService(store));

        var markdown = await export.ExportAgentsMdAsync("test-repo");

        Assert.StartsWith("# Agent instructions — test-repo", markdown);
        Assert.Contains("## Project knowledge", markdown);
        Assert.Contains("- Single dispatcher for REST+MCP", markdown);
        Assert.Contains("## Procedures", markdown);
        Assert.Contains("### Service update procedure", markdown);
        Assert.Contains("2. Update", markdown); // procedures keep their full steps
        Assert.Contains("## Rules of thumb", markdown);
        Assert.Contains("- Prefer embedded RavenDB", markdown);
        Assert.DoesNotContain("Session residue", markdown);
    }

    [Fact]
    public async Task EmptyRepo_RendersHeaderOnly()
    {
        var export = new ExportService(new InMemoryEidetStore(), new MemoryService(new InMemoryEidetStore()));

        var markdown = await export.ExportAgentsMdAsync("test-repo");

        Assert.StartsWith("# Agent instructions — test-repo", markdown);
        Assert.DoesNotContain("##", markdown);
    }
}
