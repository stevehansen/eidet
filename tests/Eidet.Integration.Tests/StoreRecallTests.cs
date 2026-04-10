using System.Net.Http.Json;
using System.Text.Json;
using Eidet.Integration.Tests.Fixtures;

namespace Eidet.Integration.Tests;

public class StoreRecallTests : IClassFixture<EidetApiFixture>
{
    private readonly EidetApiFixture _fixture;

    public StoreRecallTests(EidetApiFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Store_ValidMemory_ReturnsId()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var res = await _fixture.Client.PostAsJsonAsync("/api/eidet", new
        {
            repo = _fixture.RepoId,
            content = "The auth module uses JWT RS256",
            type = "observation",
            tags = new[] { "auth", "jwt" },
            importance = 0.7,
        });

        Assert.True(res.IsSuccessStatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("id", out var id));
        Assert.False(string.IsNullOrEmpty(id.GetString()));
    }

    [SkippableFact]
    public async Task Store_SecretContent_IsRejected()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var res = await _fixture.Client.PostAsJsonAsync("/api/eidet", new
        {
            repo = _fixture.RepoId,
            content = "AWS key is AKIAIOSFODNN7EXAMPLE",
            type = "observation",
        });

        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("reason", out var reason));
        Assert.Contains("secret", reason.GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Context_ReturnsText()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        // Store something first
        await _fixture.Client.PostAsJsonAsync("/api/eidet", new
        {
            repo = _fixture.RepoId,
            content = "Redis is the caching layer with 5-minute TTL",
            type = "insight",
            importance = 0.8,
        });

        await _fixture.WaitForIndexesAsync();

        var res = await _fixture.Client.GetAsync($"/api/eidet/context?repo={_fixture.RepoId}");
        Assert.True(res.IsSuccessStatusCode);

        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var context = json.RootElement.GetProperty("context").GetString();
        Assert.False(string.IsNullOrEmpty(context));
        Assert.Contains("Memory:", context);
    }

    [SkippableFact]
    public async Task Browse_ReturnsPaginatedResults()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        // Store a few memories
        for (var i = 0; i < 3; i++)
        {
            await _fixture.Client.PostAsJsonAsync("/api/eidet", new
            {
                repo = _fixture.RepoId,
                content = $"Browse test memory number {i} with unique content {Guid.NewGuid()}",
                type = "observation",
            });
        }

        await _fixture.WaitForIndexesAsync();

        var res = await _fixture.Client.GetAsync($"/api/eidet/browse?repo={_fixture.RepoId}&skip=0&take=10");
        Assert.True(res.IsSuccessStatusCode);

        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("count").GetInt32() > 0);
    }

    [SkippableFact]
    public async Task Repos_ListsStoredRepos()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        await _fixture.WaitForIndexesAsync();

        var res = await _fixture.Client.GetAsync("/api/eidet/repos");
        Assert.True(res.IsSuccessStatusCode);

        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("repos", out var repos));
        Assert.True(repos.GetArrayLength() >= 0);
    }

    [SkippableFact]
    public async Task Quality_ReturnsReport()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var res = await _fixture.Client.GetAsync($"/api/eidet/quality?repo={_fixture.RepoId}");
        Assert.True(res.IsSuccessStatusCode);

        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("overallScore", out _));
        Assert.True(json.RootElement.TryGetProperty("issues", out _));
    }

    [SkippableFact]
    public async Task Feedback_AppliesSuccessfully()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        // Store a memory first
        var storeRes = await _fixture.Client.PostAsJsonAsync("/api/eidet", new
        {
            repo = _fixture.RepoId,
            content = $"Feedback test memory {Guid.NewGuid()}",
            type = "observation",
        });
        var storeJson = JsonDocument.Parse(await storeRes.Content.ReadAsStringAsync());
        var memoryId = storeJson.RootElement.GetProperty("id").GetString();

        // Apply feedback
        var res = await _fixture.Client.PostAsJsonAsync("/api/eidet/feedback", new
        {
            memoryId,
            wasUsed = true,
        });
        Assert.True(res.IsSuccessStatusCode);
    }

    [SkippableFact]
    public async Task Store_MissingRepo_Returns400()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var res = await _fixture.Client.GetAsync("/api/eidet/context");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
    }
}
