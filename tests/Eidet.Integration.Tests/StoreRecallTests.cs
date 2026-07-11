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

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("Blocked", error.GetString()!, StringComparison.OrdinalIgnoreCase);
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
    public async Task Recall_StageFilter_ReturnsRequestedStagePlusNone_ExcludesOthers()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        // Exercises the real RavenDB None-as-wildcard subclause in ApplyFilters (the one piece the
        // Core unit tests can't cover — the OpenSubclause/OrElse/CloseSubclause query syntax).
        var uniq = Guid.NewGuid().ToString("N")[..8];

        async Task Store(string content, string? stage) =>
            await _fixture.Client.PostAsJsonAsync("/api/eidet", new
            {
                repo = _fixture.RepoId, content, type = "procedure", stage,
            });

        await Store($"stage filter {uniq} edit-path notes on the parser refactor", "edit");
        await Store($"stage filter {uniq} test-path notes on the parser refactor", "test");
        await Store($"stage filter {uniq} general notes on the parser refactor", null); // None

        await _fixture.WaitForIndexesAsync();

        // Poll the search until indexing has settled (both expected docs present), then assert the
        // exclusion. Robust against the embedded-RavenDB write→index race without masking a real bug:
        // a leaking filter surfaces "test-path" even after settling; a blackout never surfaces "general".
        var url = $"/api/eidet/search?repo={_fixture.RepoId}&q=stage+filter+{uniq}+parser&stage=edit&limit=20";
        var body = "";
        for (var i = 0; i < 30; i++)
        {
            var res = await _fixture.Client.GetAsync(url);
            Assert.True(res.IsSuccessStatusCode);
            body = await res.Content.ReadAsStringAsync();
            if (body.Contains("edit-path") && body.Contains("general")) break;
            await Task.Delay(300);
        }

        Assert.Contains("edit-path", body);        // requested stage
        Assert.Contains("general", body);          // stage-agnostic None wildcard
        Assert.DoesNotContain("test-path", body);  // different stage excluded
    }

    [SkippableFact]
    public async Task Curation_IfMatchConcurrency_And_Redact_RoundTrip()
    {
        Skip.IfNot(_fixture.Available, "Embedded RavenDB not available");

        var uniq = Guid.NewGuid().ToString("N")[..8];

        // Store, then GET to read the contentSha256 the response now exposes (#65).
        var storeRes = await _fixture.Client.PostAsJsonAsync("/api/eidet", new
        {
            repo = _fixture.RepoId, content = $"curation {uniq} original content about the widget", type = "insight",
        });
        var id = JsonDocument.Parse(await storeRes.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        var getRes = await _fixture.Client.GetAsync($"/api/eidet/{id}");
        Assert.True(getRes.IsSuccessStatusCode);
        var sha = JsonDocument.Parse(await getRes.Content.ReadAsStringAsync()).RootElement.GetProperty("contentSha256").GetString()!;
        Assert.False(string.IsNullOrEmpty(sha));

        // Stale If-Match → 409 (no edit applied).
        var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/eidet/{id}")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { content = $"curation {uniq} rewrite one" }),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"0000stale0000\"");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await _fixture.Client.SendAsync(stale)).StatusCode);

        // Matching If-Match → success (supersedes the original node).
        var ok = new HttpRequestMessage(HttpMethod.Put, $"/api/eidet/{id}")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { content = $"curation {uniq} rewrite two" }),
        };
        ok.Headers.TryAddWithoutValidation("If-Match", $"\"{sha}\"");
        Assert.True((await _fixture.Client.SendAsync(ok)).IsSuccessStatusCode);

        // Redact the (now superseded) original node → content scrubbed to a tombstone.
        var redactRes = await _fixture.Client.PostAsJsonAsync($"/api/eidet/{id}/redact", new { reason = "gdpr-erasure" });
        Assert.True(redactRes.IsSuccessStatusCode);

        var afterGet = await _fixture.Client.GetAsync($"/api/eidet/{id}");
        var afterContent = JsonDocument.Parse(await afterGet.Content.ReadAsStringAsync()).RootElement.GetProperty("content").GetString()!;
        Assert.StartsWith("[redacted:", afterContent);
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
