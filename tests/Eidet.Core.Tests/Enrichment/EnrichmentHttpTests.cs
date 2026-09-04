using Eidet.Core.Configuration;
using Eidet.Core.Enrichment;

namespace Eidet.Core.Tests.Enrichment;

public class EnrichmentHttpTests
{
    [Theory]
    [InlineData("https://cortex.example/v1/", "https://cortex.example")]
    [InlineData("https://cortex.example/v1", "https://cortex.example")]
    [InlineData("https://cortex.example/V1", "https://cortex.example")]
    [InlineData("http://localhost:1234/", "http://localhost:1234")]
    [InlineData("http://localhost:11434", "http://localhost:11434")]
    [InlineData(" http://localhost:1234/v1/ ", "http://localhost:1234")]
    public void NormalizeBaseUrl_DropsTrailingSlashAndV1(string input, string expected)
    {
        Assert.Equal(expected, EnrichmentHttp.NormalizeBaseUrl(input));
    }

    [Fact]
    public void NormalizeBaseUrl_KeepsAV1ThatIsNotASuffix()
    {
        Assert.Equal("http://host/v1/proxy", EnrichmentHttp.NormalizeBaseUrl("http://host/v1/proxy/"));
    }

    [Fact]
    public void CreateClient_WithApiKey_SendsBearer()
    {
        using var http = EnrichmentHttp.CreateClient("https://cortex.example/v1", "sk-test", TimeSpan.FromSeconds(1));

        Assert.Equal(new Uri("https://cortex.example"), http.BaseAddress);
        Assert.Equal("Bearer", http.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("sk-test", http.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateClient_WithoutApiKey_SendsNoAuthHeader(string? apiKey)
    {
        using var http = EnrichmentHttp.CreateClient("http://localhost:1234", apiKey, TimeSpan.FromSeconds(1));
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void CreateClient_FromBackendConfig_UsesItsUrlAndKey()
    {
        var backend = new EnrichmentBackendConfig { Url = "https://cortex.example/v1/", ApiKey = "k" };
        using var http = EnrichmentHttp.CreateClient(backend, TimeSpan.FromSeconds(1));

        Assert.Equal(new Uri("https://cortex.example"), http.BaseAddress);
        Assert.Equal("k", http.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Theory]
    [InlineData(EnrichmentProvider.Ollama, "/api/tags")]
    [InlineData(EnrichmentProvider.OpenAiCompatible, "/v1/models")]
    public void ProbePath_IsProviderSpecific(EnrichmentProvider provider, string expected)
    {
        Assert.Equal(expected, EnrichmentHttp.ProbePath(provider));
    }
}
