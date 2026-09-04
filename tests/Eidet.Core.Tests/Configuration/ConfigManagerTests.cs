using System.Reflection;
using System.Text.Json;
using Eidet.Core.Configuration;

namespace Eidet.Core.Tests.Configuration;

public class ConfigManagerTests
{
    // ConfigManager.Load() is anchored at the real user-profile config path with no
    // injectable seam, so the migration/env-override logic is exercised directly via the
    // private static methods rather than touching the filesystem (which would clobber the
    // developer's real config.json).

    private static string MigrateLegacyEnrichmentKeys(string json)
    {
        var m = typeof(ConfigManager).GetMethod("MigrateLegacyEnrichmentKeys",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, [json])!;
    }

    private static void ApplyEnvironmentOverrides(EidetConfig config)
    {
        var m = typeof(ConfigManager).GetMethod("ApplyEnvironmentOverrides",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        m.Invoke(null, [config]);
    }

    private static EnrichmentConfig DeserializeEnrichment(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        return JsonSerializer.Deserialize<EidetConfig>(json, options)!.Enrichment;
    }

    [Fact]
    public void Enrichment_ChainWithKeyAndThinking_RoundTrips()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var config = new EidetConfig();
        config.Enrichment.Enabled = true;
        config.Enrichment.Provider = EnrichmentProvider.OpenAiCompatible;
        config.Enrichment.Url = "https://cortex.example/v1";
        config.Enrichment.Model = "deepseek-v4-flash";
        config.Enrichment.ApiKey = "sk-test";
        config.Enrichment.Thinking = false;
        config.Enrichment.Fallbacks.Add(new EnrichmentBackendConfig { Provider = EnrichmentProvider.Ollama, Url = "http://localhost:11434", Model = "gemma4" });

        var json = JsonSerializer.Serialize(config, options);
        var back = JsonSerializer.Deserialize<EidetConfig>(json, options)!.Enrichment;

        Assert.Equal("sk-test", back.ApiKey);
        Assert.False(back.Thinking);
        var fallback = Assert.Single(back.Fallbacks);
        Assert.Equal(EnrichmentProvider.Ollama, fallback.Provider);
        Assert.Equal("gemma4", fallback.Model);
        Assert.Null(fallback.Thinking);
        Assert.Equal(2, back.Backends.Count);
        Assert.Same(back, back.Backends[0]);
        Assert.DoesNotContain("\"backends\"", json); // derived view, never persisted
    }

    [Fact]
    public void Enrichment_LegacyFlatConfig_HasNoFallbacks()
    {
        var enrichment = DeserializeEnrichment("""{"enrichment":{"enabled":true,"provider":"Ollama","url":"http://localhost:11434","model":"gemma4"}}""");
        Assert.Empty(enrichment.Fallbacks);
        Assert.Null(enrichment.ApiKey);
        Assert.Null(enrichment.Thinking);
        Assert.Single(enrichment.Backends);
    }

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
        Assert.False(enrichment.Enabled);
        Assert.Equal(EnrichmentProvider.Ollama, enrichment.Provider);
        Assert.Equal("http://localhost:11434", enrichment.Url);
        Assert.Equal("gemma4", enrichment.Model);
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

    // ─── Legacy enrichment-key migration ─────────────────────────────────

    [Fact]
    public void Migrate_LegacyKeysOnly_MapToNewKeys()
    {
        var json = """
            { "enrichment": { "ollamaEnabled": true, "ollamaUrl": "http://legacy:11434", "ollamaModel": "llama3" } }
            """;
        var enrichment = DeserializeEnrichment(MigrateLegacyEnrichmentKeys(json));
        Assert.True(enrichment.Enabled);
        Assert.Equal("http://legacy:11434", enrichment.Url);
        Assert.Equal("llama3", enrichment.Model);
    }

    [Fact]
    public void Migrate_BothLegacyAndNewKeys_NewWins_LegacyIgnored()
    {
        var json = """
            {
              "enrichment": {
                "ollamaEnabled": false, "ollamaUrl": "http://legacy:11434", "ollamaModel": "llama3",
                "enabled": true, "url": "http://new:1234", "model": "qwen"
              }
            }
            """;
        var enrichment = DeserializeEnrichment(MigrateLegacyEnrichmentKeys(json));
        Assert.True(enrichment.Enabled);
        Assert.Equal("http://new:1234", enrichment.Url);
        Assert.Equal("qwen", enrichment.Model);
    }

    [Fact]
    public void Migrate_NewKeysOnly_Unchanged()
    {
        var json = """
            { "enrichment": { "enabled": true, "provider": "OpenAiCompatible", "url": "http://new:1234", "model": "qwen" } }
            """;
        var enrichment = DeserializeEnrichment(MigrateLegacyEnrichmentKeys(json));
        Assert.True(enrichment.Enabled);
        Assert.Equal(EnrichmentProvider.OpenAiCompatible, enrichment.Provider);
        Assert.Equal("http://new:1234", enrichment.Url);
        Assert.Equal("qwen", enrichment.Model);
    }

    [Fact]
    public void Migrate_MalformedJson_ReturnedUntouched()
    {
        var json = "{ not valid json";
        Assert.Equal(json, MigrateLegacyEnrichmentKeys(json));
    }

    // ─── Environment-variable overrides (new wins over legacy alias) ─────

    [Fact]
    public void EnvOverride_NewVars_WinOverLegacyAliases()
    {
        var prev = (
            Environment.GetEnvironmentVariable("EIDET_ENRICHMENT_URL"),
            Environment.GetEnvironmentVariable("EIDET_OLLAMA_URL"),
            Environment.GetEnvironmentVariable("EIDET_ENRICHMENT_MODEL"),
            Environment.GetEnvironmentVariable("EIDET_OLLAMA_MODEL"));
        try
        {
            Environment.SetEnvironmentVariable("EIDET_ENRICHMENT_URL", "http://new:1234");
            Environment.SetEnvironmentVariable("EIDET_OLLAMA_URL", "http://legacy:11434");
            Environment.SetEnvironmentVariable("EIDET_ENRICHMENT_MODEL", "qwen");
            Environment.SetEnvironmentVariable("EIDET_OLLAMA_MODEL", "llama3");

            var config = new EidetConfig();
            ApplyEnvironmentOverrides(config);

            Assert.Equal("http://new:1234", config.Enrichment.Url);
            Assert.Equal("qwen", config.Enrichment.Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EIDET_ENRICHMENT_URL", prev.Item1);
            Environment.SetEnvironmentVariable("EIDET_OLLAMA_URL", prev.Item2);
            Environment.SetEnvironmentVariable("EIDET_ENRICHMENT_MODEL", prev.Item3);
            Environment.SetEnvironmentVariable("EIDET_OLLAMA_MODEL", prev.Item4);
        }
    }

    [Fact]
    public void EnvOverride_LegacyVarsOnly_StillApply()
    {
        var prev = (
            Environment.GetEnvironmentVariable("EIDET_ENRICHMENT_URL"),
            Environment.GetEnvironmentVariable("EIDET_OLLAMA_URL"),
            Environment.GetEnvironmentVariable("EIDET_ENRICHMENT_MODEL"),
            Environment.GetEnvironmentVariable("EIDET_OLLAMA_MODEL"));
        try
        {
            Environment.SetEnvironmentVariable("EIDET_ENRICHMENT_URL", null);
            Environment.SetEnvironmentVariable("EIDET_ENRICHMENT_MODEL", null);
            Environment.SetEnvironmentVariable("EIDET_OLLAMA_URL", "http://legacy:11434");
            Environment.SetEnvironmentVariable("EIDET_OLLAMA_MODEL", "llama3");

            var config = new EidetConfig();
            ApplyEnvironmentOverrides(config);

            Assert.Equal("http://legacy:11434", config.Enrichment.Url);
            Assert.Equal("llama3", config.Enrichment.Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EIDET_ENRICHMENT_URL", prev.Item1);
            Environment.SetEnvironmentVariable("EIDET_OLLAMA_URL", prev.Item2);
            Environment.SetEnvironmentVariable("EIDET_ENRICHMENT_MODEL", prev.Item3);
            Environment.SetEnvironmentVariable("EIDET_OLLAMA_MODEL", prev.Item4);
        }
    }
}
