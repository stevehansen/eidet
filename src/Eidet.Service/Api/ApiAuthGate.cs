using System.Net;
using Eidet.Core.Configuration;
using Eidet.Core.Services;

namespace Eidet.Service.Api;

/// <summary>
/// Bearer-token gate for the REST/MCP listener: looks up the scope required by
/// (method, path), validates the API key against <see cref="AuthConfig"/>, and
/// writes 401/403 directly when access is denied. Disabled configs are a no-op.
/// </summary>
internal sealed class ApiAuthGate
{
    private readonly AuthConfig _auth;

    public ApiAuthGate(AuthConfig auth) => _auth = auth;

    /// <returns>true to continue dispatch; false if the request was rejected and a response already written.</returns>
    public async Task<bool> CheckAsync(HttpListenerContext ctx, string method, string path)
    {
        if (!_auth.Enabled) return true;

        var requiredScope = ApiKeyService.GetRequiredScope(method, path);
        if (string.IsNullOrEmpty(requiredScope)) return true;

        var authHeader = ctx.Request.Headers["Authorization"];
        var rawKey = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? authHeader["Bearer ".Length..] : null;

        if (string.IsNullOrEmpty(rawKey))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Authentication required" }, 401);
            return false;
        }

        var entry = ApiKeyService.ValidateKey(_auth, rawKey);
        if (entry is null)
        {
            await HttpJson.WriteAsync(ctx, new { error = "Invalid API key" }, 401);
            return false;
        }

        if (!ApiKeyService.HasScope(entry, requiredScope))
        {
            await HttpJson.WriteAsync(ctx, new { error = "Insufficient permissions", required = requiredScope }, 403);
            return false;
        }

        return true;
    }
}
