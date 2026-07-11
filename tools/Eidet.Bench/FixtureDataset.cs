using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eidet.Bench;

/// <summary>
/// The bundled synthetic dataset: a handful of linked tasks over a fictional repo, embedded in
/// the assembly so the offline smoke/logic path needs no download. Task objects carry exactly
/// the canonical SWE-bench columns of the real release; the base/related split is conveyed by
/// the file structure (two arrays), mirroring how the release separates them by file rather
/// than by column.
/// </summary>
public sealed class FixtureDataset : ISweDatasetPort
{
    public string Name => "bundled-fixture-v1";
    public bool IsRealDataset => false;
    public bool IsAvailable => true;

    public Task<IReadOnlyList<SweTask>> LoadAsync(int limit, CancellationToken ct = default)
    {
        var file = JsonSerializer.Deserialize<FixtureFile>(EmbeddedFixture.Read("fixture.json"))
                   ?? throw new InvalidOperationException("Fixture JSON deserialized to null.");

        var baseTasks = limit > 0 ? file.Base.Take(limit) : file.Base;
        IReadOnlyList<SweTask> tasks =
        [
            .. file.Related.Select(t => t.ToTask(isRelated: true)),
            .. baseTasks.Select(t => t.ToTask(isRelated: false)),
        ];
        return Task.FromResult(tasks);
    }

    private sealed record FixtureFile(
        [property: JsonPropertyName("related")] List<FixtureTask> Related,
        [property: JsonPropertyName("base")] List<FixtureTask> Base);

    /// <summary>One task row, named exactly like the published parquet columns.</summary>
    private sealed record FixtureTask(
        [property: JsonPropertyName("repo")] string Repo,
        [property: JsonPropertyName("instance_id")] string InstanceId,
        [property: JsonPropertyName("base_commit")] string BaseCommit,
        [property: JsonPropertyName("patch")] string Patch,
        [property: JsonPropertyName("test_patch")] string TestPatch,
        [property: JsonPropertyName("problem_statement")] string ProblemStatement,
        [property: JsonPropertyName("hints_text")] string HintsText,
        [property: JsonPropertyName("created_at")] string CreatedAt,
        [property: JsonPropertyName("version")] double Version,
        [property: JsonPropertyName("FAIL_TO_PASS")] string FailToPass,
        [property: JsonPropertyName("PASS_TO_PASS")] string PassToPass,
        [property: JsonPropertyName("environment_setup_commit")] string EnvironmentSetupCommit)
    {
        public SweTask ToTask(bool isRelated) => new(
            Repo, InstanceId, BaseCommit, Patch, TestPatch, ProblemStatement, HintsText,
            CreatedAt, Version, FailToPass, PassToPass, EnvironmentSetupCommit, isRelated);
    }
}
