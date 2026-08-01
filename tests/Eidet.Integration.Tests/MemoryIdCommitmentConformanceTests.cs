using System.Net.Http.Json;
using System.Text.Json;
using Eidet.Core.Domain;
using Eidet.Integration.Tests.Fixtures;

namespace Eidet.Integration.Tests;

/// <summary>
/// Pins the RavenDB <see cref="DateTime.Kind"/> round-trip behaviour that the memory-id commitment
/// depends on. A memory id IS a truncated SHA256 over <c>content + createdAt.ToString("O")</c>, and
/// <c>"O"</c> renders differently per Kind (<c>…Z</c> for Utc, no suffix for Unspecified). So if the
/// serializer hands <c>CreatedAt</c> back with a different Kind than the writer minted the id from,
/// re-deriving the id after a read-back yields a different hash and <see cref="MemoryCommitment"/>
/// would report the entire corpus as tampered.
///
/// This test asserts the round trip against the real embedded server rather than a fake, because the
/// question is entirely about the serializer's conventions (<c>DocumentStoreFactory</c> sets none).
/// It is the conformance guard for the Kind normalization inside <see cref="MemoryIdGenerator"/>.
/// </summary>
public class MemoryIdCommitmentConformanceTests : IClassFixture<EidetApiFixture>
{
    private readonly EidetApiFixture _fixture;

    public MemoryIdCommitmentConformanceTests(EidetApiFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task StoredMemory_CreatedAtRoundTrip_ReproducesItsOwnId()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var res = await _fixture.Client.PostAsJsonAsync("/api/eidet", new
        {
            repo = _fixture.RepoId,
            content = $"commitment conformance probe {Guid.NewGuid():N} — the id must re-derive after read-back",
            type = "observation",
        });
        Assert.True(res.IsSuccessStatusCode);
        var id = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        var entry = await _fixture.Store.GetAsync(id);
        Assert.NotNull(entry);

        // Measured, not assumed: the embedded server preserves Kind=Utc across the round trip, so the
        // Kind normalization inside Generate is a no-op for the existing corpus and the commitment
        // check does NOT report every memory as tampered. Pinned here because a future serializer
        // convention change would flip this silently — it would de-boost the whole corpus at recall
        // rather than fail a build.
        Assert.Equal(DateTimeKind.Utc, entry!.CreatedAt.Kind);

        var rederived = MemoryIdGenerator.Generate(entry.RepoId, entry.Type, entry.Content, entry.CreatedAt);
        Assert.Equal(entry.Id, rederived);
    }
}
