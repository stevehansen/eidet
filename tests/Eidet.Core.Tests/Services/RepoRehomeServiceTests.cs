using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

/// <summary>
/// The authority on moving a namespace: every live memory arrives under the target repo intact and
/// live, every original is retired with a reason naming where it went, and a second run is a no-op.
/// </summary>
public class RepoRehomeServiceTests
{
    private const string From = "C--Temp-claude-scratch-wt-issues";
    private const string To = "P--Vidyano-Service";

    private static (RepoRehomeService Rehome, InMemoryEidetStore Store) Build()
    {
        var store = new InMemoryEidetStore();
        return (new RepoRehomeService(store, new MemoryService(store)), store);
    }

    private static MemoryEntry Entry(string repo, string content, MemoryType type = MemoryType.Insight)
    {
        var createdAt = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);
        return new MemoryEntry
        {
            Id = MemoryIdGenerator.Generate(repo, type, content, createdAt),
            RepoId = repo,
            Type = type,
            Content = content,
            CreatedAt = createdAt,
            Validity = new Validity { ValidFrom = createdAt },
            Importance = 0.75f,
            Tags = ["worktree", "identity"],
            Summary = "a summary that must survive the move",
        };
    }

    [Fact]
    public async Task Rehome_MovesEveryLiveMemoryToTheTarget()
    {
        var (rehome, store) = Build();
        await store.StoreAsync(Entry(From, "first stranded memory"));
        await store.StoreAsync(Entry(From, "second stranded memory"));

        var result = await rehome.RehomeAsync(From, To);

        Assert.Equal(2, result.Moved);
        Assert.Equal(0, result.Folded);
        Assert.Empty(await store.BrowseAsync(From, 0, 50));
        Assert.Equal(2, (await store.BrowseAsync(To, 0, 50)).Count);
    }

    [Fact]
    public async Task Rehome_LeavesTheCopyLive_AndRetiresOnlyTheOriginal()
    {
        // The regression: taking the copy and the retirement from one object leaves the arriving
        // memory already retired, so the move reads as successful and the corpus loses the memory.
        var (rehome, store) = Build();
        var original = Entry(From, "must arrive live");
        await store.StoreAsync(original);

        await rehome.RehomeAsync(From, To);

        var arrived = Assert.Single(await store.BrowseAsync(To, 0, 50));
        Assert.Null(arrived.Validity.ValidUntil);
        Assert.Null(arrived.ForgetReason);

        var retired = await store.GetAsync(original.Id);
        Assert.NotNull(retired);
        Assert.NotNull(retired!.Validity.ValidUntil);
        Assert.Contains(To, retired.ForgetReason);
    }

    [Fact]
    public async Task Rehome_MintsAnIdCommittedToTheTargetRepo()
    {
        var (rehome, store) = Build();
        var original = Entry(From, "id must be re-minted");
        await store.StoreAsync(original);

        await rehome.RehomeAsync(From, To);

        var arrived = Assert.Single(await store.BrowseAsync(To, 0, 50));
        Assert.NotEqual(original.Id, arrived.Id);
        Assert.StartsWith($"memories/{To}/", arrived.Id);
        Assert.True(
            MemoryIdGenerator.Matches(arrived.Id, To, arrived.Type, arrived.Content, arrived.CreatedAt),
            "a re-homed memory must satisfy its own content commitment");
    }

    [Fact]
    public async Task Rehome_PreservesTheFieldsThatCarryValue()
    {
        var (rehome, store) = Build();
        await store.StoreAsync(Entry(From, "fields must survive"));

        await rehome.RehomeAsync(From, To);

        var arrived = Assert.Single(await store.BrowseAsync(To, 0, 50));
        Assert.Equal("fields must survive", arrived.Content);
        Assert.Equal(0.75f, arrived.Importance);
        Assert.Equal("a summary that must survive the move", arrived.Summary);
        Assert.Equal(new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc), arrived.CreatedAt);
        Assert.Contains("worktree", arrived.Tags);
    }

    [Fact]
    public async Task Rehome_FoldsContentTheTargetAlreadyHolds_WithoutCopyingIt()
    {
        var (rehome, store) = Build();
        var stranded = Entry(From, "stored from both checkouts");
        await store.StoreAsync(stranded);
        await store.StoreAsync(Entry(To, "stored from both checkouts"));

        var result = await rehome.RehomeAsync(From, To);

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.Folded);
        Assert.Single(await store.BrowseAsync(To, 0, 50));

        // The whole point of the repair: the source namespace ends up empty either way, so a
        // half-emptied one cannot go on shadowing recall.
        Assert.Empty(await store.BrowseAsync(From, 0, 50));
        var retired = await store.GetAsync(stranded.Id);
        Assert.Contains("Already held by", retired!.ForgetReason);
    }

    [Fact]
    public async Task Rehome_IsANoOpOnASecondRun()
    {
        var (rehome, store) = Build();
        await store.StoreAsync(Entry(From, "moved once"));

        await rehome.RehomeAsync(From, To);
        var second = await rehome.RehomeAsync(From, To);

        Assert.Equal(0, second.Moved);
        Assert.Equal(0, second.Folded);
        Assert.Single(await store.BrowseAsync(To, 0, 50));
    }

    [Fact]
    public async Task DryRun_ReportsWithoutWriting()
    {
        var (rehome, store) = Build();
        await store.StoreAsync(Entry(From, "untouched by a dry run"));

        var result = await rehome.RehomeAsync(From, To, dryRun: true);

        Assert.Equal(1, result.Moved);
        Assert.Single(await store.BrowseAsync(From, 0, 50));
        Assert.Empty(await store.BrowseAsync(To, 0, 50));
    }

    [Fact]
    public async Task Rehome_ToItself_ChangesNothing()
    {
        var (rehome, store) = Build();
        await store.StoreAsync(Entry(To, "already home"));

        var result = await rehome.RehomeAsync(To, To);

        Assert.Equal(0, result.Moved);
        Assert.Single(await store.BrowseAsync(To, 0, 50));
    }

    [Fact]
    public async Task Rehome_AcceptsPathsAsWellAsRepoIds()
    {
        var (rehome, store) = Build();
        await store.StoreAsync(Entry(From, "addressed by path"));

        var result = await rehome.RehomeAsync(From, @"P:\Vidyano.Service");

        Assert.Equal(To, result.To);
        Assert.Single(await store.BrowseAsync(To, 0, 50));
    }
}
