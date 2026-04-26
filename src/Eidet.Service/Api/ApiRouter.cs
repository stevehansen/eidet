using System.Net;

namespace Eidet.Service.Api;

internal delegate Task RouteHandler(HttpListenerContext ctx, string path, CancellationToken ct);

/// <summary>
/// Tiny ordered route table for the REST/MCP listener. Each entry is
/// (method, path predicate, handler); the first match wins, mirroring the
/// previous chained <c>if/else</c> dispatch. Methods registered as <c>null</c>
/// match any HTTP verb (used for <c>/</c> and the embedded <c>/ui</c> assets).
/// </summary>
internal sealed class ApiRouter
{
    private readonly List<Route> _routes = new();

    public ApiRouter MapGet(string exact, RouteHandler h) => Add("GET", p => p == exact, h);
    public ApiRouter MapPost(string exact, RouteHandler h) => Add("POST", p => p == exact, h);
    public ApiRouter MapGetPrefix(string prefix, RouteHandler h) => Add("GET", p => p.StartsWith(prefix), h);
    public ApiRouter Map(string method, Func<string, bool> predicate, RouteHandler h) => Add(method, predicate, h);
    public ApiRouter MapAny(Func<string, bool> predicate, RouteHandler h) => Add(null, predicate, h);

    /// <summary>
    /// Match paths starting with <paramref name="prefix"/> (any HTTP method) and
    /// pass the suffix-after-prefix to the handler instead of the full path.
    /// </summary>
    public ApiRouter MapAnyPrefix(string prefix, RouteHandler h) => MapPrefixCore(null, prefix, null, h);

    /// <summary>
    /// Match paths starting with <paramref name="prefix"/> for the given HTTP
    /// <paramref name="method"/> and pass the suffix-after-prefix to the handler.
    /// </summary>
    public ApiRouter MapPrefix(string method, string prefix, RouteHandler h) => MapPrefixCore(method, prefix, null, h);

    /// <summary>
    /// Match paths starting with <paramref name="prefix"/> for the given HTTP
    /// <paramref name="method"/> only when the suffix passes
    /// <paramref name="suffixPredicate"/>. The handler receives the suffix.
    /// </summary>
    public ApiRouter MapPrefix(string method, string prefix, Func<string, bool> suffixPredicate, RouteHandler h) =>
        MapPrefixCore(method, prefix, suffixPredicate, h);

    private ApiRouter MapPrefixCore(string? method, string prefix, Func<string, bool>? suffixPredicate, RouteHandler h)
    {
        bool Match(string p) => p.StartsWith(prefix) && (suffixPredicate is null || suffixPredicate(p[prefix.Length..]));
        return Add(method, Match, (ctx, path, ct) => h(ctx, path[prefix.Length..], ct));
    }

    private ApiRouter Add(string? method, Func<string, bool> predicate, RouteHandler handler)
    {
        _routes.Add(new Route(method, predicate, handler));
        return this;
    }

    public async Task<bool> DispatchAsync(HttpListenerContext ctx, string method, string path, CancellationToken ct)
    {
        foreach (var route in _routes)
        {
            if (route.Method is not null && route.Method != method) continue;
            if (!route.Predicate(path)) continue;
            await route.Handler(ctx, path, ct);
            return true;
        }
        return false;
    }

    private sealed record Route(string? Method, Func<string, bool> Predicate, RouteHandler Handler);
}
