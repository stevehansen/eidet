using System.Net;
using System.Text.Json;
using Eidet.Integration.Tests.Fixtures;

namespace Eidet.Integration.Tests;

/// <summary>
/// The maintenance surface over real HTTP. The grace-window branch itself is settled in
/// <c>Eidet.Service.Tests/Api/MaintenanceRunsTests.cs</c>; what needs a live listener is that the
/// fast path still answers with the bare report — the shape every existing caller already parses —
/// and that the poll route is actually reachable rather than swallowed by a catch-all.
/// </summary>
public class MaintenanceApiTests : IClassFixture<EidetApiFixture>
{
    private readonly EidetApiFixture _fixture;

    public MaintenanceApiTests(EidetApiFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task ARunThatFinishesFast_AnswersWithTheReportItself()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var res = await _fixture.Client.PostAsync($"/api/maintenance?repo={_fixture.RepoId}", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(_fixture.RepoId, body.GetProperty("repoId").GetString());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("stages").ValueKind);
        Assert.False(body.TryGetProperty("runId", out _)); // a 200 is the report, not an envelope
    }

    [SkippableFact]
    public async Task PollingAnUnknownRun_Is404WithAReason()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var res = await _fixture.Client.GetAsync("/api/maintenance/runs/nosuchrun");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("nosuchrun", await res.Content.ReadAsStringAsync());
    }
}
