using Eidet.Core.Domain;
using Eidet.Integration.Tests.Fixtures;

namespace Eidet.Integration.Tests;

/// <summary>
/// Exercises the RavenDB translation of the unenriched-backlog queries
/// (<c>GetUnenrichedAsync</c> / <c>GetUnenrichedStatsAsync</c>) against embedded RavenDB —
/// null-equality on Summary, nested Validity.ValidUntil, IsLatest, ordering, and the
/// cross-repo stats variant all run through a collection auto-index.
/// </summary>
public class UnenrichedQueryTests : IClassFixture<EidetApiFixture>
{
    private readonly EidetApiFixture _fixture;

    public UnenrichedQueryTests(EidetApiFixture fixture) => _fixture = fixture;

    private static MemoryEntry MakeEntry(string repoId, string id, string? summary = null,
        DateTime? createdAt = null, bool isLatest = true, DateTime? validUntil = null) => new()
    {
        Id = $"memories/{repoId}/insight/{id}",
        RepoId = repoId,
        Type = MemoryType.Insight,
        Content = $"integration test content {id}",
        Summary = summary,
        CreatedAt = createdAt ?? DateTime.UtcNow.AddDays(-1),
        Validity = new Validity { ValidFrom = DateTime.UtcNow.AddDays(-2), ValidUntil = validUntil },
        IsLatest = isLatest,
    };

    /// <summary>Polls until the (auto-)index catches up with the seeded docs.</summary>
    private async Task<List<MemoryEntry>> WaitForUnenrichedAsync(string repoId, int expected)
    {
        List<MemoryEntry> result = [];
        for (var i = 0; i < 60; i++)
        {
            result = await _fixture.Store.GetUnenrichedAsync(repoId, 100);
            if (result.Count == expected) return result;
            await Task.Delay(250);
        }
        return result;
    }

    [SkippableFact]
    public async Task GetUnenriched_SelectsOnlyPending_OldestFirst()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");
        var repo = $"unenriched-{Guid.NewGuid():N}"[..24];

        var older = MakeEntry(repo, "older", createdAt: DateTime.UtcNow.AddDays(-10));
        var newer = MakeEntry(repo, "newer", createdAt: DateTime.UtcNow.AddDays(-1));
        var summarized = MakeEntry(repo, "done", summary: "has a summary");
        var superseded = MakeEntry(repo, "superseded", isLatest: false);
        var forgotten = MakeEntry(repo, "forgotten", validUntil: DateTime.UtcNow.AddHours(-1));
        foreach (var e in new[] { newer, older, summarized, superseded, forgotten })
            await _fixture.Store.StoreAsync(e);

        var result = await WaitForUnenrichedAsync(repo, 2);

        Assert.Equal(new[] { older.Id, newer.Id }, result.Select(e => e.Id).ToList());
    }

    [SkippableFact]
    public async Task GetUnenrichedStats_CountsAndFindsOldest_PerRepoAndGlobal()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");
        var repoA = $"stats-a-{Guid.NewGuid():N}"[..24];
        var repoB = $"stats-b-{Guid.NewGuid():N}"[..24];

        var oldest = DateTime.UtcNow.AddDays(-30);
        await _fixture.Store.StoreAsync(MakeEntry(repoA, "a1", createdAt: oldest));
        await _fixture.Store.StoreAsync(MakeEntry(repoA, "a2"));
        await _fixture.Store.StoreAsync(MakeEntry(repoB, "b1"));
        await WaitForUnenrichedAsync(repoA, 2);
        await WaitForUnenrichedAsync(repoB, 1);

        var statsA = await _fixture.Store.GetUnenrichedStatsAsync(repoA);
        Assert.Equal(2, statsA.Count);
        Assert.NotNull(statsA.OldestCreatedAt);
        Assert.Equal(oldest, statsA.OldestCreatedAt.Value, TimeSpan.FromSeconds(1));

        // Global stats span repos (other fixture tests may add more — lower bound only).
        var global = await _fixture.Store.GetUnenrichedStatsAsync();
        Assert.True(global.Count >= 3);
    }

    [SkippableFact]
    public async Task GetUnenrichedStats_EmptyRepo_ReturnsZeroAndNullOldest()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");
        var repo = $"stats-empty-{Guid.NewGuid():N}"[..24];

        var stats = await _fixture.Store.GetUnenrichedStatsAsync(repo);

        Assert.Equal(0, stats.Count);
        Assert.Null(stats.OldestCreatedAt);
    }
}
