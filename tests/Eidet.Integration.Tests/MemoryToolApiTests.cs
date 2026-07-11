using System.Net.Http.Json;
using System.Text.Json;
using Eidet.Integration.Tests.Fixtures;

namespace Eidet.Integration.Tests;

/// <summary>
/// End-to-end coverage of <c>POST /api/eidet/memory-tool</c>: the raw memory_20250818
/// command envelope goes over HTTP, through the translator, into the RavenDB blob store,
/// and the result (including <c>is_error</c>) is relayed back verbatim.
/// </summary>
public class MemoryToolApiTests : IClassFixture<EidetApiFixture>
{
    private readonly EidetApiFixture _fixture;

    public MemoryToolApiTests(EidetApiFixture fixture) => _fixture = fixture;

    private async Task<(bool IsError, string Text)> PostCommandAsync(object envelope)
    {
        var res = await _fixture.Client.PostAsJsonAsync(
            $"/api/eidet/memory-tool?repo={Uri.EscapeDataString(_fixture.RepoId)}", envelope);
        Assert.True(res.IsSuccessStatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("isError").GetBoolean(),
                json.RootElement.GetProperty("text").GetString()!);
    }

    [SkippableFact]
    public async Task Create_Then_View_RoundTripsOverHttp()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var created = await PostCommandAsync(new
        {
            command = "create",
            path = "/memories/http-roundtrip.md",
            file_text = "line one\nline two\n",
        });
        Assert.False(created.IsError);
        Assert.Equal("File created successfully at: /memories/http-roundtrip.md", created.Text);

        var viewed = await PostCommandAsync(new { command = "view", path = "/memories/http-roundtrip.md" });
        Assert.False(viewed.IsError);
        Assert.Contains("     1\tline one", viewed.Text);
        Assert.Contains("     2\tline two", viewed.Text);
    }

    [SkippableFact]
    public async Task SecretWrite_RelaysIsError()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var result = await PostCommandAsync(new
        {
            command = "create",
            path = "/memories/creds.md",
            file_text = "aws AKIAIOSFODNN7EXAMPLE",
        });

        Assert.True(result.IsError);
        Assert.Contains("AWS access key", result.Text);

        var view = await PostCommandAsync(new { command = "view", path = "/memories/creds.md" });
        Assert.True(view.IsError); // nothing was stored
    }

    [SkippableFact]
    public async Task TraversalPath_RelaysIsError()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var result = await PostCommandAsync(new
        {
            command = "view",
            path = "/memories/../../etc/passwd",
        });

        Assert.True(result.IsError);
        Assert.Contains("traversal", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MissingRepo_Returns400()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var res = await _fixture.Client.PostAsJsonAsync("/api/eidet/memory-tool",
            new { command = "view", path = "/memories" });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
    }
}
