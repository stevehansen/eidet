using System.Net;
using Eidet.Service.Api;

namespace Eidet.Service.Tests.Api;

public class ApiRouterTests
{
    [Fact]
    public async Task MapGet_ExactPath_FiresHandler()
    {
        var fired = false;
        var r = new ApiRouter();
        r.MapGet("/api/health", (_, _, _) => { fired = true; return Task.CompletedTask; });

        var matched = await r.DispatchAsync(null!, "GET", "/api/health", CancellationToken.None);

        Assert.True(matched);
        Assert.True(fired);
    }

    [Fact]
    public async Task MapGet_DifferentMethod_DoesNotFire()
    {
        var fired = false;
        var r = new ApiRouter();
        r.MapGet("/api/health", (_, _, _) => { fired = true; return Task.CompletedTask; });

        var matched = await r.DispatchAsync(null!, "POST", "/api/health", CancellationToken.None);

        Assert.False(matched);
        Assert.False(fired);
    }

    [Fact]
    public async Task DispatchAsync_NoRouteMatches_ReturnsFalse()
    {
        var r = new ApiRouter();
        r.MapGet("/api/health", (_, _, _) => Task.CompletedTask);

        var matched = await r.DispatchAsync(null!, "GET", "/nope", CancellationToken.None);

        Assert.False(matched);
    }

    [Fact]
    public async Task MapPrefix_StripsPrefix_PassesSuffixToHandler()
    {
        string? captured = null;
        var r = new ApiRouter();
        r.MapPrefix("GET", "/api/eidet/history/", (_, suffix, _) => { captured = suffix; return Task.CompletedTask; });

        var matched = await r.DispatchAsync(null!, "GET", "/api/eidet/history/memories/repo/insight/abc", CancellationToken.None);

        Assert.True(matched);
        Assert.Equal("memories/repo/insight/abc", captured);
    }

    [Fact]
    public async Task MapPrefix_DoesNotMatchDifferentPrefix()
    {
        var fired = false;
        var r = new ApiRouter();
        r.MapPrefix("GET", "/api/eidet/history/", (_, _, _) => { fired = true; return Task.CompletedTask; });

        var matched = await r.DispatchAsync(null!, "GET", "/api/eidet/other", CancellationToken.None);

        Assert.False(matched);
        Assert.False(fired);
    }

    [Fact]
    public async Task MapPrefix_WithSuffixPredicate_FiltersMatches()
    {
        string? captured = null;
        var r = new ApiRouter();
        r.MapPrefix("PUT", "/api/eidet/", id => !id.Contains("/links"),
            (_, suffix, _) => { captured = suffix; return Task.CompletedTask; });

        // Suffix contains /links — predicate rejects.
        var rejected = await r.DispatchAsync(null!, "PUT", "/api/eidet/memories/repo/insight/abc/links", CancellationToken.None);
        Assert.False(rejected);
        Assert.Null(captured);

        // Suffix has no /links — predicate accepts; suffix is stripped.
        var matched = await r.DispatchAsync(null!, "PUT", "/api/eidet/memories/repo/insight/abc", CancellationToken.None);
        Assert.True(matched);
        Assert.Equal("memories/repo/insight/abc", captured);
    }

    [Fact]
    public async Task MapAny_MatchesAnyVerb()
    {
        var verbs = new List<string>();
        var r = new ApiRouter();
        r.MapAny(p => p == "/x", (_, _, _) => { verbs.Add("seen"); return Task.CompletedTask; });

        Assert.True(await r.DispatchAsync(null!, "GET", "/x", CancellationToken.None));
        Assert.True(await r.DispatchAsync(null!, "POST", "/x", CancellationToken.None));
        Assert.True(await r.DispatchAsync(null!, "DELETE", "/x", CancellationToken.None));
        Assert.Equal(3, verbs.Count);
    }

    [Fact]
    public async Task MapAnyPrefix_StripsPrefix()
    {
        string? captured = null;
        var r = new ApiRouter();
        r.MapAnyPrefix("/ui/", (_, asset, _) => { captured = asset; return Task.CompletedTask; });

        var matched = await r.DispatchAsync(null!, "GET", "/ui/css/main.css", CancellationToken.None);

        Assert.True(matched);
        Assert.Equal("css/main.css", captured);
    }

    [Fact]
    public async Task DispatchAsync_FirstMatchingRouteWins()
    {
        var which = "";
        var r = new ApiRouter();
        r.MapGet("/api/eidet/quality", (_, _, _) => { which = "exact"; return Task.CompletedTask; });
        r.MapPrefix("GET", "/api/eidet/", (_, _, _) => { which = "prefix"; return Task.CompletedTask; });

        await r.DispatchAsync(null!, "GET", "/api/eidet/quality", CancellationToken.None);

        Assert.Equal("exact", which);
    }
}
