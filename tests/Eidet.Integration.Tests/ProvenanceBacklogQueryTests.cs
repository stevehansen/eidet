using Eidet.Core.Domain;
using Eidet.Core.Memory;
using Eidet.Integration.Tests.Fixtures;
using Raven.Client.Documents.Operations;

namespace Eidet.Integration.Tests;

/// <summary>
/// The RavenDB translation of the two store reads the nightly provenance repair depends on. Both make
/// claims about how RavenDB indexes and loads data that no in-memory fake can validate:
///
/// <c>GetUnprovenancedAsync</c> must match a document whose <c>Provenance</c> property is ABSENT, not
/// merely one storing "Unknown". That is the entire population it exists to drain — documents written
/// before the field existed — and it is unreachable through any write path, so it is created here by
/// deleting the property from a stored document. If RavenDB indexed a missing property as anything other
/// than null, the repair would run every night, report success, and never touch a single one of them.
///
/// <c>GetManyAsync</c> must return a null-valued entry for an id that does not exist, rather than
/// omitting the key. The citation checks distinguish "cited target is gone" from "cited target resolved",
/// and an omitted key would silently collapse the two into the latter.
/// </summary>
public class ProvenanceBacklogQueryTests : IClassFixture<EidetApiFixture>
{
    private readonly EidetApiFixture _fixture;

    public ProvenanceBacklogQueryTests(EidetApiFixture fixture) => _fixture = fixture;

    private static MemoryEntry Entry(
        string repoId, string id, string source, MemoryProvenance provenance,
        DateTime createdAt, bool isLatest = true, DateTime? validUntil = null) => new()
    {
        Id = $"memories/{repoId}/insight/{id}",
        RepoId = repoId,
        Type = MemoryType.Insight,
        Content = $"provenance backlog content {id}",
        Summary = "seeded",
        CreatedAt = createdAt,
        Validity = new Validity { ValidFrom = createdAt.AddDays(-1), ValidUntil = validUntil },
        IsLatest = isLatest,
        Source = source,
        Provenance = provenance,
    };

    private async Task<IReadOnlyList<MemoryEntry>> WaitForBacklogAsync(string repoId, int expected)
    {
        IReadOnlyList<MemoryEntry> result = [];
        for (var i = 0; i < 60; i++)
        {
            result = await _fixture.Store.GetUnprovenancedAsync(
                repoId, ProvenanceResolver.RecognizedSources, 100);
            if (result.Count == expected) return result;
            await Task.Delay(250);
        }
        return result;
    }

    [SkippableFact]
    public async Task GetUnprovenanced_MatchesAbsentField_FiltersBySource_AndOrdersOldestFirst()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");
        var repo = $"prov-{Guid.NewGuid():N}"[..24];
        var day = DateTime.UtcNow.Date;

        // The pre-field document: stored, then stripped of the property entirely.
        var preField = Entry(repo, "prefield", "intake", MemoryProvenance.Unknown, day.AddDays(-40));
        await _fixture.Store.StoreAsync(preField);
        await _fixture.Raven.Operations.SendAsync(new PatchOperation(
            preField.Id, null, new PatchRequest { Script = "delete this.Provenance;" }));

        var storedUnknown = Entry(repo, "unknown", "user", MemoryProvenance.Unknown, day.AddDays(-10));
        var unrepairable = Entry(repo, "unrepairable", "some-source-this-build-does-not-know",
            MemoryProvenance.Unknown, day.AddDays(-90));
        var established = Entry(repo, "established", "user", MemoryProvenance.UserStated, day.AddDays(-50));
        var forgotten = Entry(repo, "forgotten", "user", MemoryProvenance.Unknown, day.AddDays(-60),
            validUntil: day.AddDays(-1));
        var superseded = Entry(repo, "superseded", "user", MemoryProvenance.Unknown, day.AddDays(-70),
            isLatest: false);
        foreach (var e in new[] { storedUnknown, unrepairable, established, forgotten, superseded })
            await _fixture.Store.StoreAsync(e);

        var backlog = await WaitForBacklogAsync(repo, 2);

        // Absent field and stored "Unknown" both qualify; oldest first. Everything else is excluded:
        // an unmappable source (unrepairable), an established provenance, and anything not live.
        Assert.Equal(new[] { preField.Id, storedUnknown.Id }, backlog.Select(e => e.Id).ToList());
    }

    [SkippableFact]
    public async Task GetUnprovenanced_EmptySourceSet_MatchesNothing()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");
        var repo = $"prov-none-{Guid.NewGuid():N}"[..24];
        await _fixture.Store.StoreAsync(
            Entry(repo, "unknown", "user", MemoryProvenance.Unknown, DateTime.UtcNow.AddDays(-5)));
        await WaitForBacklogAsync(repo, 1);

        // No source is repairable, so nothing is a candidate — never "no filter, match everything".
        Assert.Empty(await _fixture.Store.GetUnprovenancedAsync(repo, [], 100));
    }

    [SkippableFact]
    public async Task GetMany_ReturnsNullValuedEntryForAMissingId()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");
        var repo = $"getmany-{Guid.NewGuid():N}"[..24];

        var present = Entry(repo, "present", "user", MemoryProvenance.UserStated, DateTime.UtcNow.AddDays(-2));
        await _fixture.Store.StoreAsync(present);
        var absentId = $"memories/{repo}/observation/deadbeef1234";

        var resolved = await _fixture.Store.GetManyAsync([present.Id, absentId]);

        Assert.Equal(2, resolved.Count);
        Assert.NotNull(resolved[present.Id]);
        Assert.Null(resolved[absentId]);
        // Ids are matched case-insensitively, as RavenDB matches them.
        Assert.NotNull(resolved[present.Id.ToUpperInvariant()]);
    }

    [SkippableFact]
    public async Task GetMany_EmptyRequest_IsNotAQuery()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");
        Assert.Empty(await _fixture.Store.GetManyAsync([]));
    }
}
