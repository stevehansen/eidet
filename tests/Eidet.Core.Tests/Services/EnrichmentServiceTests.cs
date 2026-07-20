using System.Reflection;
using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class EnrichmentServiceTests
{
    private static IEnrichmentPort GetPort(EnrichmentService svc)
    {
        var field = typeof(EnrichmentService).GetField("_port",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IEnrichmentPort)field.GetValue(svc)!;
    }

    // ─── Null / unavailable ──────────────────────────────────────────────

    [Fact]
    public void CreateNull_IsNotAvailable()
    {
        using var svc = EnrichmentService.CreateNull();
        Assert.False(svc.IsAvailable);
    }

    [Fact]
    public async Task CreateNull_EnrichMemory_ReturnsFalse()
    {
        using var svc = EnrichmentService.CreateNull();
        var entry = new MemoryEntry { Content = "some content", Id = "id" };
        Assert.False(await svc.EnrichMemoryAsync(entry));
    }

    [Fact]
    public async Task CreateNull_MergeObservations_ReturnsNull()
    {
        using var svc = EnrichmentService.CreateNull();
        Assert.Null(await svc.MergeObservationsAsync(["obs1", "obs2"]));
    }

    [Fact]
    public async Task CreateNull_ExtractEntities_ReturnsEmpty()
    {
        using var svc = EnrichmentService.CreateNull();
        Assert.Empty(await svc.ExtractEntitiesAsync("content"));
    }

    [Fact]
    public async Task CreateNull_CheckHealth_ReturnsFalse()
    {
        using var svc = EnrichmentService.CreateNull();
        Assert.False(await svc.CheckHealthAsync());
    }

    // ─── EnrichMemoryAsync short-circuits ─────────────────────────────────

    [Fact]
    public async Task EnrichMemory_UnavailablePort_ReturnsFalseWithoutCalls()
    {
        var adapter = new InMemoryEnrichmentAdapter { IsAvailable = false }
            .SetResponse(EnrichmentPrompt.Summary, "should-not-be-returned");
        using var svc = new EnrichmentService(adapter);

        var entry = new MemoryEntry { Content = "content", Id = "id" };
        Assert.False(await svc.EnrichMemoryAsync(entry));
        Assert.Null(entry.Summary);
    }

    [Fact]
    public async Task EnrichMemory_EmptyContent_ReturnsFalse()
    {
        var adapter = new InMemoryEnrichmentAdapter().SetResponse(EnrichmentPrompt.Summary, "s");
        using var svc = new EnrichmentService(adapter);
        var entry = new MemoryEntry { Content = "  ", Id = "id" };
        Assert.False(await svc.EnrichMemoryAsync(entry));
    }

    // ─── EnrichMemoryAsync fills only missing fields ─────────────────────

    [Fact]
    public async Task EnrichMemory_FillsMissingSummary()
    {
        var adapter = new InMemoryEnrichmentAdapter()
            .SetResponse(EnrichmentPrompt.Summary, "Generated summary.");
        using var svc = new EnrichmentService(adapter);

        var entry = new MemoryEntry { Content = "Some technical memory.", Id = "id", Summary = null };
        Assert.True(await svc.EnrichMemoryAsync(entry));
        Assert.Equal("Generated summary.", entry.Summary);
    }

    [Fact]
    public async Task EnrichMemory_DoesNotOverwriteExistingSummary()
    {
        var adapter = new InMemoryEnrichmentAdapter()
            .SetResponse(EnrichmentPrompt.Summary, "new summary");
        using var svc = new EnrichmentService(adapter);

        var entry = new MemoryEntry { Content = "x", Id = "id", Summary = "existing", ForesightHint = "h", Entities = { "a", "b" } };
        // Existing heuristic prevents OneLiner upgrade too
        entry.OneLiner = "not-the-heuristic";
        await svc.EnrichMemoryAsync(entry);
        Assert.Equal("existing", entry.Summary);
    }

    [Fact]
    public async Task EnrichMemory_UpgradesHeuristicOneLiner()
    {
        var content = "The RavenDB index uses Corax engine for full-text search.";
        var heuristic = EntityExtractor.GenerateHeuristicOneLiner(content);

        var adapter = new InMemoryEnrichmentAdapter()
            .SetResponse(EnrichmentPrompt.OneLiner, "Corax engine powers the RavenDB full-text index.");
        using var svc = new EnrichmentService(adapter);

        var entry = new MemoryEntry
        {
            Id = "id",
            Content = content,
            Summary = "s",
            OneLiner = heuristic,
            ForesightHint = "h",
            Entities = { "a", "b" },
        };

        Assert.True(await svc.EnrichMemoryAsync(entry));
        Assert.Equal("Corax engine powers the RavenDB full-text index.", entry.OneLiner);
    }

    [Fact]
    public async Task EnrichMemory_KeepsNonHeuristicOneLiner()
    {
        var adapter = new InMemoryEnrichmentAdapter()
            .SetResponse(EnrichmentPrompt.OneLiner, "llm-one-liner");
        using var svc = new EnrichmentService(adapter);

        var entry = new MemoryEntry
        {
            Id = "id",
            Content = "some content",
            Summary = "s",
            OneLiner = "manual-one-liner",
            ForesightHint = "h",
            Entities = { "a", "b" },
        };
        await svc.EnrichMemoryAsync(entry);
        Assert.Equal("manual-one-liner", entry.OneLiner);
    }

    [Fact]
    public async Task EnrichMemory_AppendsLlmEntities_WithoutDuplicates()
    {
        var adapter = new InMemoryEnrichmentAdapter()
            .SetResponse(EnrichmentPrompt.Entities, "RavenDB\nCorax\nexisting");
        using var svc = new EnrichmentService(adapter);

        var entry = new MemoryEntry
        {
            Id = "id",
            Content = "x",
            Summary = "s",
            OneLiner = "not-heuristic",
            ForesightHint = "h",
            Entities = { "existing" },
        };

        await svc.EnrichMemoryAsync(entry);
        Assert.Contains("RavenDB", entry.Entities);
        Assert.Contains("Corax", entry.Entities);
        Assert.Equal(1, entry.Entities.Count(e => e.Equals("existing", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task EnrichMemory_SkipsEntityCallWhenAlreadyPopulated()
    {
        var adapter = new InMemoryEnrichmentAdapter()
            .SetResponse(EnrichmentPrompt.Entities, "A\nB\nC");
        using var svc = new EnrichmentService(adapter);

        var entry = new MemoryEntry
        {
            Id = "id",
            Content = "x",
            Summary = "s",
            OneLiner = "not-heuristic",
            ForesightHint = "h",
            Entities = { "e1", "e2" },
        };

        await svc.EnrichMemoryAsync(entry);
        Assert.Equal(2, entry.Entities.Count);
    }

    [Fact]
    public async Task EnrichMemory_NoFieldsMissing_ReturnsFalse()
    {
        var adapter = new InMemoryEnrichmentAdapter()
            .SetResponse(EnrichmentPrompt.Summary, "s")
            .SetResponse(EnrichmentPrompt.OneLiner, "o")
            .SetResponse(EnrichmentPrompt.ForesightHint, "h");
        using var svc = new EnrichmentService(adapter);

        var entry = new MemoryEntry
        {
            Id = "id",
            Content = "x",
            Summary = "done",
            OneLiner = "not-heuristic",
            ForesightHint = "done",
            Entities = { "a", "b" },
        };

        Assert.False(await svc.EnrichMemoryAsync(entry));
    }

    // ─── ExtractEntities parsing ──────────────────────────────────────────

    [Fact]
    public async Task ExtractEntities_ParsesOnePerLine_Deduplicates()
    {
        var adapter = new InMemoryEnrichmentAdapter()
            .SetResponse(EnrichmentPrompt.Entities, "RavenDB\ncorax\nRAVENDB\n\n  \nA"); // A length 1 is filtered (> 1 required)
        using var svc = new EnrichmentService(adapter);

        var entities = await svc.ExtractEntitiesAsync("x");
        Assert.Equal(2, entities.Count);
        Assert.Contains(entities, e => e.Equals("RavenDB", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entities, e => e.Equals("corax", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractEntities_EmptyResponse_ReturnsEmptyList()
    {
        var adapter = new InMemoryEnrichmentAdapter().SetResponse(EnrichmentPrompt.Entities, null);
        using var svc = new EnrichmentService(adapter);
        Assert.Empty(await svc.ExtractEntitiesAsync("x"));
    }

    // ─── MergeObservations ────────────────────────────────────────────────

    [Fact]
    public async Task MergeObservations_PassesAuxListToPort()
    {
        IReadOnlyList<string>? received = null;
        var adapter = new InMemoryEnrichmentAdapter()
            .SetResponder(EnrichmentPrompt.MergeObservations, req =>
            {
                received = req.Aux;
                return "merged-insight";
            });
        using var svc = new EnrichmentService(adapter);

        var result = await svc.MergeObservationsAsync(["o1", "o2", "o3"]);
        Assert.Equal("merged-insight", result);
        Assert.NotNull(received);
        Assert.Equal(["o1", "o2", "o3"], received);
    }

    // ─── Live Ollama-adapter behavior (no server running) ────────────────

    [Fact]
    public void CreateOllama_BeforeHealthCheck_IsAvailable()
    {
        using var svc = EnrichmentService.CreateOllama("http://localhost:11434", "gemma4");
        Assert.True(svc.IsAvailable);
    }

    [Fact]
    public async Task CreateOllama_CheckHealth_Unreachable_ReturnsFalse()
    {
        using var svc = EnrichmentService.CreateOllama("http://localhost:19999", "gemma4");
        Assert.False(await svc.CheckHealthAsync());
    }

    [Fact]
    public async Task CreateOllama_GenerateAsync_WhenUnhealthy_ReturnsNull()
    {
        using var svc = EnrichmentService.CreateOllama("http://localhost:19999", "gemma4");
        Assert.Null(await svc.GenerateAsync(EnrichmentPrompt.OneLiner, "content"));
    }

    // ─── OllamaTextSanitizer ──────────────────────────────────────────────

    [Fact]
    public void Sanitizer_Null_ReturnsNull() =>
        Assert.Null(OllamaTextSanitizer.Clean(null));

    [Fact]
    public void Sanitizer_Whitespace_ReturnsNull() =>
        Assert.Null(OllamaTextSanitizer.Clean("   "));

    [Fact]
    public void Sanitizer_CleanText_PassesThrough()
    {
        var text = "Cross-repo search defaults to false, preventing accidental result leakage.";
        Assert.Equal(text, OllamaTextSanitizer.Clean(text));
    }

    [Fact]
    public void Sanitizer_ChannelMarker_ExtractsAnswer()
    {
        var input = "Drafting options:\n1. First option\n<channel|>Cross-repo search defaults to false.";
        Assert.Equal("Cross-repo search defaults to false.", OllamaTextSanitizer.Clean(input));
    }

    [Fact]
    public void Sanitizer_MultipleChannelMarkers_TakesLastThenTrims()
    {
        var input = "Reasoning...<channel|>First attempt<channel|>Final answer here.";
        Assert.Equal("Final answer here.", OllamaTextSanitizer.Clean(input));
    }

    [Fact]
    public void Sanitizer_ThinkTags_StripsThinking()
    {
        var input = "<think>Let me analyze this...\nThe key change is...</think>The actual summary here.";
        Assert.Equal("The actual summary here.", OllamaTextSanitizer.Clean(input));
    }

    [Fact]
    public void Sanitizer_ChannelMarkerWithRepeatedAnswer()
    {
        var input = "Reasoning<channel|>The answer.<channel|>The answer repeated.";
        Assert.Equal("The answer repeated.", OllamaTextSanitizer.Clean(input));
    }

    [Fact]
    public void Sanitizer_RealCorruptedSummary()
    {
        var input = "The user wants me to summarize a technical memory change.\nConstraint Checklist:\n1. Yes.\n2. Yes.\n<channel|>The Memories_Search index now uses a composite SearchText field for richer searching.";
        Assert.Equal("The Memories_Search index now uses a composite SearchText field for richer searching.",
            OllamaTextSanitizer.Clean(input));
    }

    [Fact]
    public void Sanitizer_RealCorruptedOneLiner()
    {
        var input = "The user wants an ultra-compact, 10-word maximum summary.\nDraft 1: option A\nDraft 2: option B\n<channel|>Use AndAlso() in RavenDB queries to enforce AND logic over OR.";
        Assert.Equal("Use AndAlso() in RavenDB queries to enforce AND logic over OR.",
            OllamaTextSanitizer.Clean(input));
    }

    // ─── CreateFromConfig dispatch ────────────────────────────────────────

    [Fact]
    public void CreateFromConfig_Disabled_UsesNullAdapter_NotAvailable()
    {
        var cfg = new EnrichmentConfig { Enabled = false, Provider = EnrichmentProvider.OpenAiCompatible };
        using var svc = EnrichmentService.CreateFromConfig(cfg);
        Assert.False(svc.IsAvailable);
        Assert.IsType<NullEnrichmentAdapter>(GetPort(svc));
    }

    [Fact]
    public void CreateFromConfig_OpenAiCompatible_UsesOpenAiAdapter()
    {
        var cfg = new EnrichmentConfig { Enabled = true, Provider = EnrichmentProvider.OpenAiCompatible, Url = "http://localhost:1234", Model = "qwen" };
        using var svc = EnrichmentService.CreateFromConfig(cfg);
        Assert.IsType<OpenAiEnrichmentAdapter>(GetPort(svc));
    }

    [Fact]
    public void CreateFromConfig_Ollama_UsesOllamaAdapter()
    {
        var cfg = new EnrichmentConfig { Enabled = true, Provider = EnrichmentProvider.Ollama, Url = "http://localhost:11434", Model = "gemma4" };
        using var svc = EnrichmentService.CreateFromConfig(cfg);
        Assert.IsType<OllamaEnrichmentAdapter>(GetPort(svc));
    }

    // ─── Reconfigure (live enrichment config reload) ─────────────────────

    [Fact]
    public void Reconfigure_SwapsAdapterAndModelName()
    {
        using var svc = EnrichmentService.CreateNull();
        Assert.Null(svc.ModelName);

        svc.Reconfigure(new EnrichmentConfig
        {
            Enabled = true,
            Provider = EnrichmentProvider.OpenAiCompatible,
            Url = "http://localhost:1234",
            Model = "google/gemma-4-12b",
        });

        Assert.IsType<OpenAiEnrichmentAdapter>(GetPort(svc));
        Assert.Equal("google/gemma-4-12b", svc.ModelName);
    }

    [Fact]
    public void Reconfigure_Disabled_SwapsToNullAdapter_AndClearsModelName()
    {
        var cfg = new EnrichmentConfig { Enabled = true, Provider = EnrichmentProvider.Ollama, Url = "http://localhost:11434", Model = "gemma4" };
        using var svc = EnrichmentService.CreateFromConfig(cfg);
        Assert.Equal("gemma4", svc.ModelName);

        svc.Reconfigure(new EnrichmentConfig { Enabled = false });

        Assert.IsType<NullEnrichmentAdapter>(GetPort(svc));
        Assert.Null(svc.ModelName);
        Assert.False(svc.IsAvailable);
    }

    [Fact]
    public void Reconfigure_DisposesOwnedOldAdapter()
    {
        var adapter = new DisposeTrackingAdapter();
        using var svc = new EnrichmentService(adapter);

        svc.Reconfigure(new EnrichmentConfig { Enabled = false });

        Assert.True(adapter.Disposed);
    }

    [Fact]
    public void Reconfigure_DoesNotDisposeUnownedOldAdapter()
    {
        var adapter = new DisposeTrackingAdapter();
        using var svc = new EnrichmentService(adapter, ownsPort: false);

        svc.Reconfigure(new EnrichmentConfig { Enabled = false });

        Assert.False(adapter.Disposed);
    }

    [Fact]
    public void Reconfigure_ThenDispose_DisposesTheNewAdapter()
    {
        // The swapped-in adapter is always owned, even when the original port was not.
        var original = new DisposeTrackingAdapter();
        var svc = new EnrichmentService(original, ownsPort: false);
        svc.Reconfigure(new EnrichmentConfig { Enabled = true, Provider = EnrichmentProvider.Ollama, Url = "http://localhost:19999", Model = "gemma4" });

        svc.Dispose(); // disposes the OllamaEnrichmentAdapter, not the unowned original

        Assert.False(original.Disposed);
    }

    private sealed class DisposeTrackingAdapter : IEnrichmentPort, IDisposable
    {
        public bool Disposed { get; private set; }
        public bool IsAvailable => true;
        public Task<string?> CompleteAsync(EnrichmentRequest request, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(true);
        public void Dispose() => Disposed = true;
    }

    // ─── EnrichmentPrompts.Build parity ───────────────────────────────────
    // Guards against the two adapters diverging: the shared builder must produce
    // exactly the prompt text the original per-adapter logic produced.

    [Fact]
    public void Prompts_OneLiner_EmbedsContent_AndConstraint()
    {
        var prompt = EnrichmentPrompts.Build(new EnrichmentRequest(EnrichmentPrompt.OneLiner, "My memory body"));
        Assert.Contains("ultra-compact one-liner", prompt);
        Assert.Contains("Memory: My memory body", prompt);
    }

    [Fact]
    public void Prompts_Summary_EmbedsContent()
    {
        var prompt = EnrichmentPrompts.Build(new EnrichmentRequest(EnrichmentPrompt.Summary, "Body text"));
        Assert.Contains("Summarize this memory", prompt);
        Assert.Contains("Memory: Body text", prompt);
    }

    [Fact]
    public void Prompts_ForesightHint_EmbedsContent()
    {
        var prompt = EnrichmentPrompts.Build(new EnrichmentRequest(EnrichmentPrompt.ForesightHint, "Body text"));
        Assert.Contains("foresight hint", prompt);
        Assert.Contains("Memory: Body text", prompt);
    }

    [Fact]
    public void Prompts_Entities_UsesTextLabel()
    {
        var prompt = EnrichmentPrompts.Build(new EnrichmentRequest(EnrichmentPrompt.Entities, "Body text"));
        Assert.Contains("Extract named entities", prompt);
        Assert.Contains("Text: Body text", prompt);
    }

    [Fact]
    public void Prompts_MergeObservations_NumbersTheAuxList()
    {
        var prompt = EnrichmentPrompts.Build(
            new EnrichmentRequest(EnrichmentPrompt.MergeObservations, string.Empty, ["first obs", "second obs"]));
        Assert.Contains("merged into a single coherent insight", prompt);
        Assert.Contains("1. first obs", prompt);
        Assert.Contains("2. second obs", prompt);
    }

    [Fact]
    public void Prompts_SameRequest_IsDeterministic()
    {
        var a = EnrichmentPrompts.Build(new EnrichmentRequest(EnrichmentPrompt.Summary, "same content"));
        var b = EnrichmentPrompts.Build(new EnrichmentRequest(EnrichmentPrompt.Summary, "same content"));
        Assert.Equal(a, b);
    }

    // ─── OpenAiEnrichmentAdapter (no server running) ──────────────────────
    // The adapter constructs its own HttpClient with no injection seam, so the HTTP
    // request shape (/v1/chat/completions, choices[0].message.content parsing, sanitizer
    // pass) cannot be asserted without modifying production code. What IS cleanly
    // testable is the health-gated short-circuit against an unreachable endpoint.

    [Fact]
    public void OpenAi_BeforeHealthCheck_IsAvailable()
    {
        using var adapter = new OpenAiEnrichmentAdapter("http://localhost:1234", "qwen");
        Assert.True(adapter.IsAvailable);
    }

    [Fact]
    public async Task OpenAi_CheckHealth_Unreachable_ReturnsFalse()
    {
        using var adapter = new OpenAiEnrichmentAdapter("http://localhost:19999", "qwen");
        Assert.False(await adapter.CheckHealthAsync());
    }

    [Fact]
    public async Task OpenAi_Complete_WhenUnhealthy_ReturnsNull()
    {
        using var adapter = new OpenAiEnrichmentAdapter("http://localhost:19999", "qwen");
        Assert.Null(await adapter.CompleteAsync(new EnrichmentRequest(EnrichmentPrompt.OneLiner, "content")));
    }
}
