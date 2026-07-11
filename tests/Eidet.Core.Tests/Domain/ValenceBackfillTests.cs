using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Domain;

namespace Eidet.Core.Tests.Domain;

/// <summary>
/// Backfill / zero-migration guarantee: <c>Neutral = 0</c> means every pre-existing document — one
/// written before the field existed — loads as "no stance" for free. Proven via the CLR default and a
/// JSON round-trip that omits the property (the on-disk shape of a legacy document).
/// </summary>
public class ValenceBackfillTests
{
    // Mirror RavenDB's on-disk shape (enums stored by name, not integer).
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void New_entry_defaults_to_neutral()
    {
        Assert.Equal(Valence.Neutral, new MemoryEntry().Valence);
    }

    [Fact]
    public void Enum_default_is_neutral()
    {
        Assert.Equal(0, (int)Valence.Neutral);
        Assert.Equal(Valence.Neutral, default(Valence));
    }

    [Fact]
    public void Deserializing_a_document_without_valence_backfills_to_neutral()
    {
        // A legacy document written before the field was added carries no Valence property.
        const string legacyJson = """
            { "Id": "memories/repo-a/insight/legacy", "RepoId": "repo-a", "Type": "Insight",
              "Content": "The deployment pipeline runs migrations before start" }
            """;

        var entry = JsonSerializer.Deserialize<MemoryEntry>(legacyJson, Json);

        Assert.NotNull(entry);
        Assert.Equal(Valence.Neutral, entry!.Valence);
    }

    [Fact]
    public void Valence_survives_a_json_round_trip()
    {
        var entry = new MemoryEntry
        {
            Id = "memories/repo-a/heuristic/dead",
            RepoId = "repo-a",
            Type = MemoryType.Heuristic,
            Valence = Valence.Refuting,
            Content = "Tried BulkInsert inside the mutation ctx — corrupts recall-cache invalidation",
        };

        var restored = JsonSerializer.Deserialize<MemoryEntry>(JsonSerializer.Serialize(entry, Json), Json);

        Assert.NotNull(restored);
        Assert.Equal(Valence.Refuting, restored!.Valence);
    }
}
