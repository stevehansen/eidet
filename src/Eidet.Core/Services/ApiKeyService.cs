using System.Security.Cryptography;
using Eidet.Core.Configuration;

namespace Eidet.Core.Services;

public static class ApiKeyService
{
    private const string KeyPrefix = "eidet_";
    private const int KeyLength = 32; // 32 random bytes → 43 base64url chars

    public static readonly string[] ValidScopes = ["read:all", "write:observations", "write:all", "admin"];

    /// <summary>
    /// Creates a new API key. Returns the raw key (show once) and the entry to store in config.
    /// </summary>
    public static (string RawKey, ApiKeyEntry Entry) CreateKey(string name, List<string>? scopes = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var randomBytes = RandomNumberGenerator.GetBytes(KeyLength);
        var rawKey = KeyPrefix + Convert.ToBase64String(randomBytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var entry = new ApiKeyEntry
        {
            Id = id,
            Name = name,
            KeyHash = HashKey(rawKey),
            Scopes = scopes ?? ["read:all", "write:all"],
            CreatedAt = DateTime.UtcNow,
        };

        return (rawKey, entry);
    }

    /// <summary>
    /// Validates a raw API key against stored entries. Returns the matching entry or null.
    /// </summary>
    public static ApiKeyEntry? ValidateKey(AuthConfig auth, string rawKey)
    {
        if (!auth.Enabled || auth.ApiKeys.Count == 0)
            return null;

        var hash = HashKey(rawKey);
        return auth.ApiKeys.FirstOrDefault(k =>
            string.Equals(k.KeyHash, hash, StringComparison.Ordinal));
    }

    /// <summary>
    /// Checks if a key entry has the required scope.
    /// </summary>
    public static bool HasScope(ApiKeyEntry entry, string requiredScope)
    {
        if (entry.Scopes.Contains("admin"))
            return true;

        if (entry.Scopes.Contains(requiredScope))
            return true;

        // write:all implies write:observations
        if (requiredScope == "write:observations" && entry.Scopes.Contains("write:all"))
            return true;

        return false;
    }

    /// <summary>
    /// Determines the required scope for a given HTTP method and path.
    /// </summary>
    public static string GetRequiredScope(string method, string path)
    {
        // Health and status are always public
        if (path == "/api/health" || path == "/api/status")
            return "";

        // Write operations
        if (method is "POST" or "PUT" or "DELETE")
        {
            if (path == "/api/maintenance" || path.StartsWith("/api/config"))
                return "admin";

            if (path == "/api/eidet" || path == "/api/eidet/intake")
                return "write:observations";

            return "write:all";
        }

        // Everything else is read
        return "read:all";
    }

    internal static string HashKey(string rawKey)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
