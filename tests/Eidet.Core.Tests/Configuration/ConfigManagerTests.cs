using Eidet.Core.Configuration;

namespace Eidet.Core.Tests.Configuration;

public class ConfigManagerTests
{
    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var config = new EidetConfig();
        Assert.Equal(19380, config.Service.Port);
        Assert.Equal("127.0.0.1", config.Service.BindAddress);
        Assert.Equal(StorageMode.External, config.Storage.Mode);
        Assert.Equal("http://localhost:8080", config.Storage.RavenUrl);
        Assert.Equal("Eidet", config.Storage.DatabaseName);
    }

    [Fact]
    public void GetConfigDir_ReturnsNonEmpty()
    {
        var dir = ConfigManager.GetConfigDir();
        Assert.False(string.IsNullOrEmpty(dir));
    }

    [Fact]
    public void GetConfigPath_EndsWithConfigJson()
    {
        var path = ConfigManager.GetConfigPath();
        Assert.True(path.EndsWith("config.json"), $"Expected path ending with config.json, got: {path}");
    }

    [Fact]
    public void EnrichmentConfig_Defaults()
    {
        var enrichment = new EnrichmentConfig();
        Assert.False(enrichment.OllamaEnabled);
        Assert.Equal("http://localhost:11434", enrichment.OllamaUrl);
        Assert.Equal("gemma4", enrichment.OllamaModel);
        Assert.True(enrichment.AutoOneLiner);
        Assert.True(enrichment.AutoForesight);
        Assert.True(enrichment.AutoConsolidation);
    }

    [Fact]
    public void MemoryConfig_Defaults()
    {
        var memory = new MemoryConfig();
        Assert.Equal(20, memory.L1Count);
        Assert.Equal(500, memory.L1MaxTokens);
        Assert.Equal(0.92f, memory.DuplicateThreshold, 0.001f);
        Assert.Equal(90, memory.ObservationRetentionDays);
        Assert.True(memory.AutoIntakeOnFirstSession);
        Assert.True(memory.CrossRepoRecallEnabled);
    }

    [Fact]
    public void MaintenanceConfig_Defaults()
    {
        var maintenance = new MaintenanceConfig();
        Assert.Equal(24, maintenance.IntervalHours);
        Assert.Equal(6, maintenance.ConsolidationIntervalHours);
    }
}
