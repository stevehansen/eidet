using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Storage;

namespace Eidet.Service.Tests;

public class HealthMonitorTests
{
    private const string RavenUrl = "http://localhost:8080";

    private static EnrichmentConfig Enrichment(bool enabled, EnrichmentProvider provider, string model, string url) =>
        new() { Enabled = enabled, Provider = provider, Model = model, Url = url };

    private static EnrichmentConfig Disabled() =>
        Enrichment(false, EnrichmentProvider.Ollama, "gemma4", "http://localhost:11434");

    [Theory]
    [InlineData(EnrichmentProvider.Ollama, "/api/tags")]
    [InlineData(EnrichmentProvider.OpenAiCompatible, "/v1/models")]
    public void ProbePath_IsProviderSpecific(EnrichmentProvider provider, string expected)
    {
        Assert.Equal(expected, EnrichmentHttp.ProbePath(provider));
    }

    [Fact]
    public void CurrentState_ReflectsInitialState()
    {
        using var cts = new CancellationTokenSource();
        using var monitor = new HealthMonitor(
            new StubStore(healthy: true), Disabled(), RavenUrl,
            initialEnrichmentHealthy: false, cts.Token);

        var state = monitor.CurrentState;
        Assert.True(state.RavenDbHealthy);
        Assert.False(state.EnrichmentHealthy);
    }

    [Fact]
    public void CurrentState_EnrichmentEnabled_ReflectsInitialHealth()
    {
        using var cts = new CancellationTokenSource();
        using var monitor = new HealthMonitor(
            new StubStore(healthy: true),
            Enrichment(true, EnrichmentProvider.OpenAiCompatible, "gemma4", "http://localhost:1234"), RavenUrl,
            initialEnrichmentHealthy: true, cts.Token);

        var state = monitor.CurrentState;
        Assert.True(state.RavenDbHealthy);
        Assert.True(state.EnrichmentHealthy);
    }

    [Fact]
    public void ReconfigureEnrichment_FiresStatusChanged_WithNewTarget()
    {
        using var cts = new CancellationTokenSource();
        using var monitor = new HealthMonitor(
            new StubStore(healthy: true),
            Enrichment(true, EnrichmentProvider.Ollama, "gemma4", "http://localhost:11434"), RavenUrl,
            initialEnrichmentHealthy: true, cts.Token);

        var events = new List<(string Component, bool Healthy, string Detail)>();
        monitor.OnStatusChanged += (c, h, d) => events.Add((c, h, d));

        monitor.ReconfigureEnrichment(
            Enrichment(true, EnrichmentProvider.OpenAiCompatible, "google/gemma-4-12b", "http://localhost:1234"),
            healthy: true);

        var e = Assert.Single(events);
        Assert.Equal("Enrichment", e.Component);
        Assert.True(e.Healthy);
        Assert.Contains("google/gemma-4-12b", e.Detail);
        Assert.Contains("http://localhost:1234", e.Detail);
        Assert.True(monitor.CurrentState.EnrichmentHealthy);
    }

    [Fact]
    public void ReconfigureEnrichment_WithFallbacks_NamesTheChain()
    {
        using var cts = new CancellationTokenSource();
        using var monitor = new HealthMonitor(
            new StubStore(healthy: true), Disabled(), RavenUrl,
            initialEnrichmentHealthy: false, cts.Token);

        var events = new List<(string Component, bool Healthy, string Detail)>();
        monitor.OnStatusChanged += (c, h, d) => events.Add((c, h, d));

        var chain = Enrichment(true, EnrichmentProvider.OpenAiCompatible, "deepseek", "https://cortex.example/v1");
        chain.Fallbacks.Add(new EnrichmentBackendConfig { Provider = EnrichmentProvider.Ollama, Url = "http://localhost:11434", Model = "gemma4" });
        monitor.ReconfigureEnrichment(chain, healthy: true);

        var e = Assert.Single(events);
        Assert.Contains("deepseek @ https://cortex.example/v1", e.Detail);
        Assert.Contains("+1 fallback", e.Detail);
    }

    [Fact]
    public void ReconfigureEnrichment_Disabled_ReportsDisabled()
    {
        using var cts = new CancellationTokenSource();
        using var monitor = new HealthMonitor(
            new StubStore(healthy: true),
            Enrichment(true, EnrichmentProvider.Ollama, "gemma4", "http://localhost:11434"), RavenUrl,
            initialEnrichmentHealthy: true, cts.Token);

        var events = new List<(string Component, bool Healthy, string Detail)>();
        monitor.OnStatusChanged += (c, h, d) => events.Add((c, h, d));

        monitor.ReconfigureEnrichment(Disabled(), healthy: false);

        var e = Assert.Single(events);
        Assert.True(e.Healthy); // disabled is not a problem state
        Assert.Contains("Disabled", e.Detail);
        Assert.False(monitor.CurrentState.EnrichmentHealthy);
    }

    [Fact]
    public void ReconfigureEnrichment_UnhealthyBackend_ReportsUnavailable()
    {
        using var cts = new CancellationTokenSource();
        using var monitor = new HealthMonitor(
            new StubStore(healthy: true), Disabled(), RavenUrl,
            initialEnrichmentHealthy: false, cts.Token);

        var events = new List<(string Component, bool Healthy, string Detail)>();
        monitor.OnStatusChanged += (c, h, d) => events.Add((c, h, d));

        monitor.ReconfigureEnrichment(
            Enrichment(true, EnrichmentProvider.OpenAiCompatible, "qwen", "http://localhost:1234"),
            healthy: false);

        var e = Assert.Single(events);
        Assert.False(e.Healthy);
        Assert.Contains("Unavailable", e.Detail);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var monitor = new HealthMonitor(
            new StubStore(healthy: true),
            Enrichment(true, EnrichmentProvider.Ollama, "gemma4", "http://localhost:11434"), RavenUrl,
            initialEnrichmentHealthy: false, cts.Token);

        monitor.Dispose();
        // Should not throw on double dispose
        monitor.Dispose();
    }

    [Fact]
    public void HealthState_Record_Equality()
    {
        var a = new HealthMonitor.HealthState(true, false);
        var b = new HealthMonitor.HealthState(true, false);
        var c = new HealthMonitor.HealthState(false, false);
        // record field rename guard: second positional is EnrichmentHealthy

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public async Task OnStatusChanged_FiresWhenRavenDbGoesDown()
    {
        using var cts = new CancellationTokenSource();
        var store = new StubStore(healthy: true);
        using var monitor = new HealthMonitor(
            store, Disabled(), RavenUrl,
            initialEnrichmentHealthy: false, cts.Token);

        var events = new List<(string Component, bool Healthy, string Detail)>();
        monitor.OnStatusChanged += (c, h, d) => events.Add((c, h, d));

        // RavenDB goes down
        store.Healthy = false;
        monitor.Start();

        // The timer has a 10s initial delay; wait up to 15 seconds for the first tick.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (events.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(500);

        Assert.Single(events);
        Assert.Equal("RavenDB", events[0].Component);
        Assert.False(events[0].Healthy);
        Assert.Contains("Unreachable", events[0].Detail);
    }

    [Fact]
    public async Task OnStatusChanged_FiresWhenRavenDbRecovers()
    {
        using var cts = new CancellationTokenSource();
        var store = new StubStore(healthy: false);
        // The monitor assumes RavenDB is healthy at start, so a false store will trigger "down" first,
        // then we flip to healthy to trigger "recovered"
        using var monitor = new HealthMonitor(
            store, Disabled(), RavenUrl,
            initialEnrichmentHealthy: false, cts.Token);

        var events = new List<(string Component, bool Healthy, string Detail)>();
        monitor.OnStatusChanged += (c, h, d) => events.Add((c, h, d));

        monitor.Start();

        // Wait for first tick (RavenDB goes down)
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (events.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(500);

        Assert.Single(events);
        Assert.False(events[0].Healthy);

        // Now RavenDB recovers
        store.Healthy = true;

        deadline = DateTime.UtcNow.AddSeconds(35);
        while (events.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(500);

        Assert.Equal(2, events.Count);
        Assert.True(events[1].Healthy);
        Assert.Contains("Connected", events[1].Detail);
    }

    [Fact]
    public async Task OnStatusChanged_DoesNotFireWhenStatusUnchanged()
    {
        using var cts = new CancellationTokenSource();
        var store = new StubStore(healthy: true);
        using var monitor = new HealthMonitor(
            store, Disabled(), RavenUrl,
            initialEnrichmentHealthy: false, cts.Token);

        var events = new List<(string Component, bool Healthy, string Detail)>();
        monitor.OnStatusChanged += (c, h, d) => events.Add((c, h, d));

        monitor.Start();

        // Wait past first tick — store is healthy and initial state is healthy, so no event
        await Task.Delay(12_000);

        Assert.Empty(events);
    }

    /// <summary>
    /// Minimal IEidetStore stub for health monitor tests.
    /// Only TestConnectionAsync is used.
    /// </summary>
    private sealed class StubStore : IEidetStore
    {
        public bool Healthy { get; set; }

        public StubStore(bool healthy) => Healthy = healthy;

        public Task<bool> TestConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult(Healthy);

        // Unused stubs below — HealthMonitor only calls TestConnectionAsync
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
        public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default) => Task.FromResult("");
        public Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ForgetAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<List<MemoryEntry>> FullTextSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) => Task.FromResult(new List<MemoryEntry>());
        public Task<List<MemoryEntry>> VectorSearchAsync(IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) => Task.FromResult(new List<MemoryEntry>());
        public Task<MemoryEntry?> FindDuplicateAsync(string repoId, string content, float threshold, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
        public Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(string repoId, CancellationToken ct = default) => Task.FromResult(new Dictionary<MemoryType, int>());
        public Task<List<MemoryEntry>> GetTopScoredAsync(string repoId, MemoryType[] types, int limit, CancellationToken ct = default) => Task.FromResult(new List<MemoryEntry>());
        public Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default) => Task.FromResult<DatabaseInfo?>(null);
        public Task EnsureIndexesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<MemoryEntry>> BrowseAsync(string repoId, int skip, int take, MemoryType? type = null, CancellationToken ct = default) => Task.FromResult(new List<MemoryEntry>());
        public Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default) => Task.FromResult("");
        public Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default) => Task.FromResult(new List<MemoryLayer>());
        public Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default) => Task.FromResult<MemoryLayer?>(null);
        public Task<List<MemoryEntry>> GetByLayerIdAsync(string layerId, CancellationToken ct = default) => Task.FromResult(new List<MemoryEntry>());
        public Task<bool> HardDeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    }
}
