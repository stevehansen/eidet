using Eidet.Core.Domain;
using Eidet.Integration.Tests.Fixtures;

namespace Eidet.Integration.Tests;

/// <summary>
/// Exercises the RavenDB translation of the cue-anchor lookup (<c>FindByEntitiesAsync</c>) against
/// embedded RavenDB. Worth its own integration coverage because the query rests on assumptions the
/// unit tests cannot check: that <c>WhereIn</c> over the <c>Entities</c> array in
/// <c>Memories_Search</c> does any-element term matching under the KeywordAnalyzer, and that it ANDs
/// correctly with the repo and validity predicates. In production a failure here is SILENT — cue
/// expansion is best-effort and swallows exceptions — so without this test a broken translation would
/// look exactly like "no related memories", forever.
/// </summary>
public class CueAnchorQueryTests : IClassFixture<EidetApiFixture>
{
    private readonly EidetApiFixture _fixture;

    public CueAnchorQueryTests(EidetApiFixture fixture) => _fixture = fixture;

    private static MemoryEntry MakeEntry(
        string repoId, string id, string[] entities,
        bool isLatest = true, DateTime? validUntil = null) => new()
    {
        Id = $"memories/{repoId}/insight/{id}",
        RepoId = repoId,
        Type = MemoryType.Insight,
        Content = $"integration test content {id}",
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        Validity = new Validity { ValidFrom = DateTime.UtcNow.AddDays(-2), ValidUntil = validUntil },
        IsLatest = isLatest,
        Entities = entities.ToList(),
    };

    /// <summary>Polls until the index catches up with the seeded docs.</summary>
    private async Task<IReadOnlyList<MemoryEntry>> WaitForCueMatchesAsync(
        string repoId, string[] cues, int expected, params string[] exclude)
    {
        IReadOnlyList<MemoryEntry> result = [];
        for (var i = 0; i < 60; i++)
        {
            result = await _fixture.Store.FindByEntitiesAsync([repoId], cues, exclude, 50);
            if (result.Count == expected) return result;
            await Task.Delay(250);
        }
        return result;
    }

    [SkippableFact]
    public async Task FindByEntities_MatchesAnySharedEntity_ExcludingPoolAndInvalid()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");
        var repo = $"cue-{Guid.NewGuid():N}"[..24];

        var sharesFirst = MakeEntry(repo, "sharesFirst", ["RavenDB", "Corax"]);
        var sharesSecond = MakeEntry(repo, "sharesSecond", ["Ollama"]);
        var inPool = MakeEntry(repo, "inPool", ["RavenDB"]);
        var unrelated = MakeEntry(repo, "unrelated", ["Postgres"]);
        var noEntities = MakeEntry(repo, "noEntities", []);
        var superseded = MakeEntry(repo, "superseded", ["RavenDB"], isLatest: false);
        var forgotten = MakeEntry(repo, "forgotten", ["RavenDB"], validUntil: DateTime.UtcNow.AddHours(-1));
        foreach (var e in new[] { sharesFirst, sharesSecond, inPool, unrelated, noEntities, superseded, forgotten })
            await _fixture.Store.StoreAsync(e);

        // Two cues; "inPool" is excluded as an existing pool member.
        var result = await WaitForCueMatchesAsync(repo, ["RavenDB", "Ollama"], 2, inPool.Id);

        Assert.Equal(
            new[] { sharesFirst.Id, sharesSecond.Id }.OrderBy(x => x),
            result.Select(e => e.Id).OrderBy(x => x));
    }

    [SkippableFact]
    public async Task FindByEntities_DoesNotCrossRepoScope()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");
        var mine = $"cue-mine-{Guid.NewGuid():N}"[..24];
        var theirs = $"cue-other-{Guid.NewGuid():N}"[..24];

        var here = MakeEntry(mine, "here", ["SharedEntity"]);
        var elsewhere = MakeEntry(theirs, "elsewhere", ["SharedEntity"]);
        await _fixture.Store.StoreAsync(here);
        await _fixture.Store.StoreAsync(elsewhere);

        var result = await WaitForCueMatchesAsync(mine, ["SharedEntity"], 1);

        Assert.Equal([here.Id], result.Select(e => e.Id));
    }

    /// <summary>
    /// Regression guard for the bug this suite caught on its first run: the query matched NOTHING
    /// because KeywordAnalyzer preserves case while the term lookup did not. Both sides now lower-case,
    /// so enrichment casing cannot decide reachability.
    /// </summary>
    [SkippableFact]
    public async Task FindByEntities_CueCasingDiffersFromStoredEntity_StillMatches()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");
        var repo = $"cue-case-{Guid.NewGuid():N}"[..24];

        var stored = MakeEntry(repo, "stored", ["RavenDB"]);
        await _fixture.Store.StoreAsync(stored);

        var result = await WaitForCueMatchesAsync(repo, ["ravendb"], 1);

        Assert.Equal([stored.Id], result.Select(e => e.Id));
    }

    [SkippableFact]
    public async Task FindByEntities_NoCues_ReturnsEmptyWithoutQuerying()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");
        var repo = $"cue-empty-{Guid.NewGuid():N}"[..24];
        await _fixture.Store.StoreAsync(MakeEntry(repo, "any", ["RavenDB"]));

        Assert.Empty(await _fixture.Store.FindByEntitiesAsync([repo], [], [], 50));
        Assert.Empty(await _fixture.Store.FindByEntitiesAsync([repo], ["RavenDB"], [], 0));
    }
}
