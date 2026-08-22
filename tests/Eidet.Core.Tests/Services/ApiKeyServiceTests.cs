using Eidet.Core.Configuration;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class ApiKeyServiceTests
{
    [Fact]
    public void CreateKey_ReturnsKeyWithPrefix()
    {
        var (rawKey, entry) = ApiKeyService.CreateKey("test-key");
        Assert.StartsWith("eidet_", rawKey);
        Assert.True(rawKey.Length > 20);
    }

    [Fact]
    public void CreateKey_SetsEntryFields()
    {
        var (_, entry) = ApiKeyService.CreateKey("my-key", ["read:all"]);
        Assert.Equal("my-key", entry.Name);
        Assert.NotEmpty(entry.Id);
        Assert.NotEmpty(entry.KeyHash);
        Assert.Single(entry.Scopes);
        Assert.Equal("read:all", entry.Scopes[0]);
    }

    [Fact]
    public void CreateKey_DefaultScopes()
    {
        var (_, entry) = ApiKeyService.CreateKey("default-scopes");
        Assert.Contains("read:all", entry.Scopes);
        Assert.Contains("write:all", entry.Scopes);
    }

    [Fact]
    public void CreateKey_UniqueKeys()
    {
        var (key1, _) = ApiKeyService.CreateKey("key1");
        var (key2, _) = ApiKeyService.CreateKey("key2");
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void ValidateKey_MatchesCorrectKey()
    {
        var (rawKey, entry) = ApiKeyService.CreateKey("test");
        var auth = new AuthConfig { Enabled = true, ApiKeys = [entry] };

        var result = ApiKeyService.ValidateKey(auth, rawKey);
        Assert.NotNull(result);
        Assert.Equal(entry.Id, result.Id);
    }

    [Fact]
    public void ValidateKey_RejectsWrongKey()
    {
        var (_, entry) = ApiKeyService.CreateKey("test");
        var auth = new AuthConfig { Enabled = true, ApiKeys = [entry] };

        var result = ApiKeyService.ValidateKey(auth, "eidet_wrongkey123");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateKey_ReturnsNullWhenDisabled()
    {
        var (rawKey, entry) = ApiKeyService.CreateKey("test");
        var auth = new AuthConfig { Enabled = false, ApiKeys = [entry] };

        var result = ApiKeyService.ValidateKey(auth, rawKey);
        Assert.Null(result);
    }

    [Fact]
    public void HasScope_AdminHasAllScopes()
    {
        var entry = new ApiKeyEntry { Scopes = ["admin"] };
        Assert.True(ApiKeyService.HasScope(entry, "read:all"));
        Assert.True(ApiKeyService.HasScope(entry, "write:all"));
        Assert.True(ApiKeyService.HasScope(entry, "write:observations"));
        Assert.True(ApiKeyService.HasScope(entry, "admin"));
    }

    [Fact]
    public void HasScope_WriteAllImpliesWriteObservations()
    {
        var entry = new ApiKeyEntry { Scopes = ["write:all"] };
        Assert.True(ApiKeyService.HasScope(entry, "write:observations"));
        Assert.True(ApiKeyService.HasScope(entry, "write:all"));
        Assert.False(ApiKeyService.HasScope(entry, "read:all"));
    }

    [Fact]
    public void HasScope_ReadOnlyCannotWrite()
    {
        var entry = new ApiKeyEntry { Scopes = ["read:all"] };
        Assert.True(ApiKeyService.HasScope(entry, "read:all"));
        Assert.False(ApiKeyService.HasScope(entry, "write:all"));
        Assert.False(ApiKeyService.HasScope(entry, "admin"));
    }

    [Fact]
    public void GetRequiredScope_HealthIsPublic()
    {
        Assert.Equal("", ApiKeyService.GetRequiredScope("GET", "/api/health"));
        Assert.Equal("", ApiKeyService.GetRequiredScope("GET", "/api/status"));
    }

    [Fact]
    public void GetRequiredScope_ReadOperations()
    {
        Assert.Equal("read:all", ApiKeyService.GetRequiredScope("GET", "/api/eidet/search"));
        Assert.Equal("read:all", ApiKeyService.GetRequiredScope("GET", "/api/eidet/context"));
    }

    [Fact]
    public void GetRequiredScope_WriteOperations()
    {
        Assert.Equal("write:observations", ApiKeyService.GetRequiredScope("POST", "/api/eidet"));
        Assert.Equal("write:observations", ApiKeyService.GetRequiredScope("POST", "/api/eidet/intake"));
    }

    [Fact]
    public void GetRequiredScope_AdminOperations()
    {
        Assert.Equal("admin", ApiKeyService.GetRequiredScope("POST", "/api/maintenance"));
        Assert.Equal("admin", ApiKeyService.GetRequiredScope("POST", "/api/config/enrichment/reload"));
    }

    /// <summary>
    /// Polling a run is a read, but the report it hands back is the same operator surface as the
    /// run itself — so the maintenance paths are admin whatever the method, and must not fall
    /// through to the read:all default.
    /// </summary>
    [Fact]
    public void GetRequiredScope_PollingAMaintenanceRunIsAdmin()
    {
        Assert.Equal("admin", ApiKeyService.GetRequiredScope("GET", "/api/maintenance/runs/abc123"));
    }

    [Fact]
    public void GetRequiredScope_UIIsPublic()
    {
        Assert.Equal("", ApiKeyService.GetRequiredScope("GET", "/ui"));
        Assert.Equal("", ApiKeyService.GetRequiredScope("GET", "/ui/"));
        Assert.Equal("", ApiKeyService.GetRequiredScope("GET", "/ui/app.js"));
        Assert.Equal("", ApiKeyService.GetRequiredScope("GET", "/ui/app.css"));
        Assert.Equal("", ApiKeyService.GetRequiredScope("GET", "/ui/index.html"));
    }

    [Fact]
    public void GetRequiredScope_BrowseAndReposNeedRead()
    {
        Assert.Equal("read:all", ApiKeyService.GetRequiredScope("GET", "/api/eidet/repos"));
        Assert.Equal("read:all", ApiKeyService.GetRequiredScope("GET", "/api/eidet/browse"));
        Assert.Equal("read:all", ApiKeyService.GetRequiredScope("GET", "/api/eidet/graph"));
    }

    [Fact]
    public void HashKey_Deterministic()
    {
        var hash1 = ApiKeyService.HashKey("eidet_test123");
        var hash2 = ApiKeyService.HashKey("eidet_test123");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashKey_DifferentForDifferentKeys()
    {
        var hash1 = ApiKeyService.HashKey("eidet_key1");
        var hash2 = ApiKeyService.HashKey("eidet_key2");
        Assert.NotEqual(hash1, hash2);
    }
}
