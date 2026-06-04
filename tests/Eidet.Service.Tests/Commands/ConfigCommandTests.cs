using Eidet.Core.Configuration;
using Eidet.Service.Commands;

namespace Eidet.Service.Tests.Commands;

public class ConfigCommandTests
{
    [Fact]
    public void GetValue_KnownKey_ReturnsValue()
    {
        var config = new EidetConfig();
        var value = ConfigHelper.GetValue(config, "service.port");
        Assert.Equal("19380", value);
    }

    [Fact]
    public void GetValue_UnknownKey_ReturnsNull()
    {
        var config = new EidetConfig();
        Assert.Null(ConfigHelper.GetValue(config, "nonexistent.key"));
    }

    [Fact]
    public void SetValue_KnownKey_UpdatesConfig()
    {
        var config = new EidetConfig();
        var result = ConfigHelper.SetValue(config, "service.port", "9999");
        Assert.True(result);
        Assert.Equal(9999, config.Service.Port);
    }

    [Fact]
    public void SetValue_UnknownKey_ReturnsFalse()
    {
        var config = new EidetConfig();
        Assert.False(ConfigHelper.SetValue(config, "nonexistent.key", "value"));
    }

    [Fact]
    public void SetValue_BooleanKey_ParsesCorrectly()
    {
        var config = new EidetConfig();
        ConfigHelper.SetValue(config, "enrichment.ollamaEnabled", "True");
        Assert.True(config.Enrichment.Enabled);
    }

    [Fact]
    public void SetValue_FloatKey_ParsesCorrectly()
    {
        var config = new EidetConfig();
        ConfigHelper.SetValue(config, "memory.duplicateThreshold", "0.85");
        Assert.Equal(0.85f, config.Memory.DuplicateThreshold, 0.001f);
    }

    [Fact]
    public void SetValue_EnumKey_ParsesCorrectly()
    {
        var config = new EidetConfig();
        ConfigHelper.SetValue(config, "storage.mode", "Embedded");
        Assert.Equal(StorageMode.Embedded, config.Storage.Mode);
    }

    [Fact]
    public void GetAllValues_ReturnsAllKeys()
    {
        var config = new EidetConfig();
        var pairs = ConfigHelper.GetAllValues(config);

        Assert.True(pairs.Count >= 20); // We have ~23 config keys
        Assert.Contains(pairs, p => p.Key == "service.port");
        Assert.Contains(pairs, p => p.Key == "enrichment.ollamaModel");
        Assert.Contains(pairs, p => p.Key == "storage.mode");
    }

    [Fact]
    public void GetValue_CaseInsensitive()
    {
        var config = new EidetConfig();
        Assert.Equal("19380", ConfigHelper.GetValue(config, "Service.Port"));
        Assert.Equal("19380", ConfigHelper.GetValue(config, "SERVICE.PORT"));
    }

    [Fact]
    public void SetValue_StringKey_SetsValue()
    {
        var config = new EidetConfig();
        ConfigHelper.SetValue(config, "enrichment.ollamaUrl", "http://custom:11434");
        Assert.Equal("http://custom:11434", config.Enrichment.Url);
    }

    [Fact]
    public void SetValue_LegacyOllamaUrlAlias_MapsToSameValueAsNewKey()
    {
        var config = new EidetConfig();
        ConfigHelper.SetValue(config, "enrichment.ollamaUrl", "http://aliased:9999");
        Assert.Equal("http://aliased:9999", ConfigHelper.GetValue(config, "enrichment.url"));
        Assert.Equal("http://aliased:9999", ConfigHelper.GetValue(config, "enrichment.ollamaUrl"));
    }

    [Fact]
    public void SetValue_NewUrlKey_ReadableViaLegacyAlias()
    {
        var config = new EidetConfig();
        ConfigHelper.SetValue(config, "enrichment.url", "http://viaNew:7777");
        Assert.Equal("http://viaNew:7777", ConfigHelper.GetValue(config, "enrichment.ollamaUrl"));
    }

    [Fact]
    public void SetValue_LegacyEnabledAndModelAliases_MapToNewKeys()
    {
        var config = new EidetConfig();
        ConfigHelper.SetValue(config, "enrichment.ollamaEnabled", "True");
        ConfigHelper.SetValue(config, "enrichment.ollamaModel", "qwen");
        Assert.Equal("True", ConfigHelper.GetValue(config, "enrichment.enabled"));
        Assert.Equal("qwen", ConfigHelper.GetValue(config, "enrichment.model"));
    }

    [Fact]
    public void SetValue_ProviderKey_ParsesEnum()
    {
        var config = new EidetConfig();
        Assert.True(ConfigHelper.SetValue(config, "enrichment.provider", "OpenAiCompatible"));
        Assert.Equal(EnrichmentProvider.OpenAiCompatible, config.Enrichment.Provider);
    }

    [Fact]
    public void GetValue_AuthEnabled_ReturnsDefault()
    {
        var config = new EidetConfig();
        Assert.Equal("False", ConfigHelper.GetValue(config, "auth.enabled"));
    }

    [Fact]
    public void SetValue_AuthEnabled_ParsesCorrectly()
    {
        var config = new EidetConfig();
        ConfigHelper.SetValue(config, "auth.enabled", "True");
        Assert.True(config.Auth.Enabled);
    }

    [Fact]
    public void GetValue_AuthRequireForNonLocalhost_ReturnsDefault()
    {
        var config = new EidetConfig();
        Assert.Equal("True", ConfigHelper.GetValue(config, "auth.requireForNonLocalhost"));
    }

    [Fact]
    public void GetAllValues_IncludesAuthKeys()
    {
        var config = new EidetConfig();
        var pairs = ConfigHelper.GetAllValues(config);
        Assert.Contains(pairs, p => p.Key == "auth.enabled");
        Assert.Contains(pairs, p => p.Key == "auth.requireForNonLocalhost");
    }
}
