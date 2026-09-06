using Eidet.Core.Domain;
using Eidet.Integration.Tests.Fixtures;

namespace Eidet.Integration.Tests;

/// <summary>
/// The RavenDB translation of "which repos hold memories". No in-memory fake can catch what this pins
/// down: the previous implementation projected <c>RepoId</c> off the search index and called
/// <c>Distinct</c> on the result, which only ever saw the first page of *documents*. On a real corpus
/// of 23k entries that reported 27 of 93 repos and looked like a complete answer — and the repos a
/// truncated scan omits are the quiet ones, which is exactly where a stranded namespace hides.
///
/// It lives here rather than beside the in-memory fakes because the claim is about RavenDB: that the
/// `Memories_CountByType` reduce index is a complete and current answer to "which repos hold live
/// memories", including after a repo's last memory is retired rather than deleted. A fake hands back
/// whatever it was given, so it can only restate the assertion.
/// </summary>
public class RepoEnumerationQueryTests : IClassFixture<EidetApiFixture>
{
    private readonly EidetApiFixture _fixture;

    public RepoEnumerationQueryTests(EidetApiFixture fixture) => _fixture = fixture;

    private static MemoryEntry Entry(string repoId, int n)
    {
        var createdAt = DateTime.UtcNow.AddDays(-n);
        var content = $"repo enumeration content {repoId} {n}";
        return new MemoryEntry
        {
            Id = MemoryIdGenerator.Generate(repoId, MemoryType.Insight, content, createdAt),
            RepoId = repoId,
            Type = MemoryType.Insight,
            Content = content,
            Summary = "seeded",
            CreatedAt = createdAt,
            Validity = new Validity { ValidFrom = createdAt },
            IsLatest = true,
        };
    }

    private async Task<Dictionary<string, int>> WaitForCountsAsync(string[] repos)
    {
        var counts = new Dictionary<string, int>();
        for (var i = 0; i < 60; i++)
        {
            counts = await _fixture.Store.GetLiveCountsByRepoAsync();
            if (repos.All(counts.ContainsKey)) return counts;
            await Task.Delay(250);
        }
        return counts;
    }

    [SkippableFact]
    public async Task EveryRepoIsListed_EvenTheOneHoldingASingleMemory()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var tag = Guid.NewGuid().ToString("n")[..8];
        var loud = $"loud-{tag}";
        var quiet = $"quiet-{tag}";

        // A lopsided pair: one repo with many memories, one with a single memory. The seed deliberately
        // stays small. Overrunning the old 1000-document page was tried and reverted — it is not a
        // deterministic reproduction anyway (index order decides which documents land on the page), and
        // this fixture's database is shared with the classes that poll for background-generated
        // embeddings, which a flood of documents starves into a timeout. What is pinned here is the
        // contract a first-page scan cannot promise at any corpus size: enumeration is exhaustive and
        // the counts are exact. The field evidence — 27 of 93 repos reported — is in the class comment.
        for (var i = 0; i < 60; i++)
            await _fixture.Store.StoreAsync(Entry(loud, i));
        await _fixture.Store.StoreAsync(Entry(quiet, 0));

        var counts = await WaitForCountsAsync([loud, quiet]);

        Assert.Equal(60, counts[loud]);
        Assert.Equal(1, counts[quiet]);

        var ids = await _fixture.Store.GetDistinctRepoIdsAsync();
        Assert.Contains(loud, ids);
        Assert.Contains(quiet, ids);
    }

    [SkippableFact]
    public async Task RetiredMemoriesDoNotKeepAnEmptiedRepoListed()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        // What `repo rehome` leaves behind: every memory retired, none deleted. The namespace must
        // stop being reported as holding memories, or the repair looks like it did nothing.
        var repo = $"emptied-{Guid.NewGuid():N}"[..20];
        var entry = Entry(repo, 1);
        await _fixture.Store.StoreAsync(entry);
        await WaitForCountsAsync([repo]);

        await _fixture.Store.ForgetAsync(entry.Id);

        for (var i = 0; i < 60; i++)
        {
            var counts = await _fixture.Store.GetLiveCountsByRepoAsync();
            if (!counts.ContainsKey(repo)) return;
            await Task.Delay(250);
        }

        Assert.Fail($"{repo} still reported live memories after its only memory was forgotten");
    }
}
