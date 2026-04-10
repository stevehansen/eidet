using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class OllamaServiceTests
{
    [Fact]
    public void RecommendedModels_NotEmpty()
    {
        Assert.NotEmpty(OllamaService.RecommendedModels);
    }

    [Fact]
    public void RecommendedModels_ContainsGemma4()
    {
        Assert.Contains("gemma4", OllamaService.RecommendedModels);
    }

    [Fact]
    public void FormatSize_Bytes()
    {
        Assert.Equal("512 B", OllamaService.FormatSize(512));
    }

    [Fact]
    public void FormatSize_Kilobytes()
    {
        Assert.Equal("1.5 KB", OllamaService.FormatSize(1536));
    }

    [Fact]
    public void FormatSize_Megabytes()
    {
        Assert.Equal("1.5 MB", OllamaService.FormatSize(1572864));
    }

    [Fact]
    public void FormatSize_Gigabytes()
    {
        Assert.Equal("2.0 GB", OllamaService.FormatSize(2147483648));
    }

    [Fact]
    public async Task IsAvailable_BadUrl_ReturnsFalse()
    {
        using var svc = new OllamaService("http://localhost:19999");
        Assert.False(await svc.IsAvailableAsync());
    }

    [Fact]
    public async Task ListModels_BadUrl_ReturnsEmpty()
    {
        using var svc = new OllamaService("http://localhost:19999");
        var models = await svc.ListModelsAsync();
        Assert.Empty(models);
    }

    [Fact]
    public async Task HasModel_BadUrl_ReturnsFalse()
    {
        using var svc = new OllamaService("http://localhost:19999");
        Assert.False(await svc.HasModelAsync("gemma4"));
    }

    [Fact]
    public async Task SuggestModel_BadUrl_ReturnsFirstRecommended()
    {
        using var svc = new OllamaService("http://localhost:19999");
        var (model, isInstalled) = await svc.SuggestModelAsync();
        Assert.Equal(OllamaService.RecommendedModels[0], model);
        Assert.False(isInstalled);
    }

    [Fact]
    public void PullProgress_Percent_ZeroTotal_ReturnsZero()
    {
        var progress = new PullProgress { Total = 0, Completed = 100 };
        Assert.Equal(0, progress.Percent);
    }

    [Fact]
    public void PullProgress_Percent_CalculatesCorrectly()
    {
        var progress = new PullProgress { Total = 200, Completed = 100 };
        Assert.Equal(50.0, progress.Percent);
    }
}
