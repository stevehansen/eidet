using System.Text.Json;
using Eidet.Integration.Tests.Fixtures;

namespace Eidet.Integration.Tests;

public class HealthTests : IClassFixture<EidetApiFixture>
{
    private readonly EidetApiFixture _fixture;

    public HealthTests(EidetApiFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Health_ReturnsOk()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var res = await _fixture.Client.GetAsync("/api/health");
        Assert.True(res.IsSuccessStatusCode);

        var json = await res.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("version").GetString()));
    }

    [SkippableFact]
    public async Task Status_ReturnsVersion()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var res = await _fixture.Client.GetAsync("/api/status");
        Assert.True(res.IsSuccessStatusCode);

        var json = await res.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("version", out _));
    }

    [SkippableFact]
    public async Task NotFound_Returns404()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var res = await _fixture.Client.GetAsync("/api/nonexistent");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
    }
}
