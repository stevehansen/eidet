using Eidet.Core.Configuration;

namespace Eidet.Core.Tests.Configuration;

public class AuthConfigTests
{
    [Fact]
    public void AuthConfig_Defaults()
    {
        var auth = new AuthConfig();
        Assert.False(auth.Enabled);
        Assert.True(auth.RequireForNonLocalhost);
        Assert.Empty(auth.ApiKeys);
    }

    [Fact]
    public void ApiKeyEntry_Defaults()
    {
        var entry = new ApiKeyEntry();
        Assert.Equal("", entry.Id);
        Assert.Equal("", entry.Name);
        Assert.Equal("", entry.KeyHash);
        Assert.Empty(entry.Scopes);
    }

    [Fact]
    public void EidetConfig_HasAuthSection()
    {
        var config = new EidetConfig();
        Assert.NotNull(config.Auth);
        Assert.False(config.Auth.Enabled);
    }
}
