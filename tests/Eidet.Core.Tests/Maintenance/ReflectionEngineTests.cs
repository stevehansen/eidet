using System.Text.Json;
using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.LooseEnds;
using Eidet.Core.Maintenance;
using Eidet.Core.Memory;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Core.Tests.LooseEnds;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// Behavioural contract for the ACE-style <see cref="ReflectionEngine"/> — the synthesis counterpart
/// to <see cref="ConsolidationEngine"/>. Mints NET-NEW itemized memories from positive feedback residue
/// (net-echoed memories, Done loose ends, Contradicted drift verdicts) through one gated LLM call.
///
/// The load-bearing invariants: a reflected memory is BORN PROVISIONAL (Provenance=Reflection, trust
/// &lt; 1.0 — it must earn trust via echoes); dry-run previews without writing; the feature degrades to a
/// clean no-op when the model is offline; every proposal is run through the mandatory secret+signal
/// write gate (its text is LLM-fresh, unlike consolidation's pre-gated observation text); a refuting
/// near-duplicate survives (contradictions are kept) while a same-polarity near-duplicate is skipped;
/// and — the anti-laundering core — a memory derived from any below-full-trust contributor inherits
/// that contributor's provenance, NOT the trusted <see cref="MemoryProvenance.Reflection"/> stamp.
/// </summary>
public class ReflectionEngineTests
{
    private const string Repo = "repo-a";

    // A single valid Insight proposal — the canned model reply for the happy paths.
    private const string OneInsightJson =
        """[{"content":"Redis connection pooling stays stable under sustained production load across restarts","type":"insight","valence":"neutral","tags":["redis","pooling"]}]""";

    private static MemoryEntry Echoed(
        string idSuffix,
        MemoryProvenance provenance = MemoryProvenance.AgentInferred,
        int echo = 5, int fizzle = 0,
        MemoryType type = MemoryType.Observation,
        string? content = null) => new()
    {
        Id = $"memories/{Repo}/{type.ToString().ToLowerInvariant()}/{idSuffix}",
        RepoId = Repo,
        Type = type,
        Content = content ?? $"observation {idSuffix} recorded redis pooling behavior under production load",
        Provenance = provenance,
        EchoCount = echo,
        FizzleCount = fizzle,
        Importance = 0.6f,
        // Firmly in the past so it sits BEHIND any live-run cursor on a second pass.
        CreatedAt = DateTime.UtcNow.AddDays(-10),
        LastAccessedAt = DateTime.UtcNow.AddDays(-10),
        Validity = new Validity { ValidFrom = DateTime.UtcNow.AddDays(-10) },
        IsLatest = true,
    };

    private static MemoryEntry Insight(string idSuffix, Valence valence, string content) => new()
    {
        Id = $"memories/{Repo}/insight/{idSuffix}",
        RepoId = Repo,
        Type = MemoryType.Insight,
        Valence = valence,
        Content = content,
        Importance = 0.7f,
        CreatedAt = DateTime.UtcNow.AddDays(-5),
        Validity = new Validity { ValidFrom = DateTime.UtcNow.AddDays(-5) },
        IsLatest = true,
    };

    private static LooseEnd End(string id, LooseEndState state, ResolutionKind? resolution, string? promotedTo = null) => new()
    {
        Id = $"looseends/{Repo}/{id}",
        RepoId = Repo,
        Note = $"parked note {id} about a follow-up worth remembering after the task closed",
        State = state,
        Resolution = resolution,
        PromotedToMemoryId = promotedTo,
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
        ResolvedAt = state == LooseEndState.Resolved ? DateTimeOffset.UtcNow.AddHours(-1) : null,
    };

    private static EnrichmentService Enrichment(string? reflectResponse, bool available = true) =>
        new(new InMemoryEnrichmentAdapter { IsAvailable = available }.SetResponse(EnrichmentPrompt.Reflect, reflectResponse));

    private static ReflectionEngine Engine(
        IEidetStore store, EnrichmentService enrichment,
        ILooseEndStore? looseEnds = null, ReflectionConfig? config = null) =>
        new(store, enrichment, new MemoryService(store), looseEnds, config);

    private static string JsonStr(string s) => JsonSerializer.Serialize(s);

    // ─── 1. End-to-end happy path ─────────────────────────────────────────

    [Fact]
    public async Task Reflect_mints_one_provisional_reflection_memory_from_net_echoed_residue()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Echoed("src1"));
        using var enrichment = Enrichment(OneInsightJson);
        var engine = Engine(store, enrichment);

        var result = await engine.ReflectAsync(Repo);

        Assert.Equal(1, result.MemoriesCreated);
        var minted = Assert.Single(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
        Assert.Equal(MemoryProvenance.Reflection, minted.Provenance);
        Assert.Equal("reflection", minted.Source);
        Assert.Contains($"memories/{Repo}/observation/src1", minted.DerivedFrom);
        // Born provisional: a reflected memory must EARN trust via echoes, not be born trusted.
        Assert.True(MemoryTrust.Factor(minted) < 1.0,
            $"reflected memory trust ({MemoryTrust.Factor(minted)}) must be below full trust");
        Assert.Equal(0.5, MemoryTrust.Factor(minted), precision: 12); // Reflection floor, zero feedback
    }

    // ─── 2. Dry-run previews without writing ──────────────────────────────

    [Fact]
    public async Task DryRun_returns_candidates_writes_nothing_leaves_cursor_null()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Echoed("src1"));
        using var enrichment = Enrichment(OneInsightJson);
        var engine = Engine(store, enrichment);

        var result = await engine.ReflectAsync(Repo, dryRun: true);

        Assert.Single(result.Candidates);
        Assert.Equal(0, result.MemoriesCreated);
        Assert.Empty(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
        Assert.Null(await store.GetLastReflectedAtAsync(Repo)); // dry-run never advances the coverage cursor
    }

    // ─── 3. Offline degradation ───────────────────────────────────────────

    [Fact]
    public async Task UnavailableAdapter_noops_and_writes_nothing()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Echoed("src1"));
        using var enrichment = Enrichment(OneInsightJson, available: false);
        var engine = Engine(store, enrichment);

        var result = await engine.ReflectAsync(Repo);

        Assert.Equal(0, result.MemoriesCreated);
        Assert.Empty(result.Candidates);
        Assert.Empty(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
        // Regression (B1): an offline live run must NOT advance the coverage cursor — otherwise the
        // residue it never actually reflected would fall behind the watermark and be skipped forever.
        Assert.Null(await store.GetLastReflectedAtAsync(Repo));
    }

    [Fact]
    public async Task NullEnrichmentService_noops_and_writes_nothing()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Echoed("src1"));
        using var enrichment = EnrichmentService.CreateNull();
        var engine = Engine(store, enrichment);

        var result = await engine.ReflectAsync(Repo);

        Assert.Equal(0, result.MemoriesCreated);
        Assert.Empty(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
    }

    // ─── 5. Mandatory write gate on LLM-fresh text ────────────────────────

    [Theory]
    [InlineData("AWS key AKIAIOSFODNN7EXAMPLE must never be committed to the repository")] // secret pattern
    [InlineData("nope")]                                                                   // too short / low signal
    public async Task Proposal_that_fails_the_write_gate_is_dropped_but_valid_siblings_survive(string badContent)
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Echoed("src1"));
        var json =
            "[{\"content\":" + JsonStr(badContent) + ",\"type\":\"insight\",\"valence\":\"neutral\",\"tags\":[]}," +
            "{\"content\":\"Connection-pool warmup at boot removes the first-request latency spike in production\",\"type\":\"insight\",\"valence\":\"neutral\",\"tags\":[\"perf\"]}]";
        using var enrichment = Enrichment(json);
        var engine = Engine(store, enrichment);

        var result = await engine.ReflectAsync(Repo);

        // The mandatory secret+signal gate drops the bad proposal; the clean one still mints.
        Assert.Equal(1, result.MemoriesCreated);
        var minted = Assert.Single(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
        Assert.DoesNotContain("AKIA", minted.Content);
        Assert.DoesNotContain(result.Candidates, c => c.Content == badContent);
    }

    // ─── 6. Valence conflict guard ────────────────────────────────────────

    [Fact]
    public async Task Refuting_proposal_near_duplicate_of_an_affirming_memory_is_still_written()
    {
        var affirming = Insight("standing", Valence.Affirming,
            "Redis connection pooling stays stable under sustained production load across restarts");
        var store = new SeededDuplicateStore(affirming);
        await store.StoreAsync(Echoed("src1"));
        // A refuting near-duplicate is a CONTRADICTION we want to keep alongside the affirming claim.
        var json =
            """[{"content":"Redis connection pooling does NOT stay stable under load — it deadlocks the worker pool","type":"insight","valence":"refuting","tags":["redis"]}]""";
        using var enrichment = Enrichment(json);
        var engine = Engine(store, enrichment);

        var result = await engine.ReflectAsync(Repo);

        Assert.Equal(1, result.MemoriesCreated);
        var minted = Assert.Single(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
        Assert.Equal(Valence.Refuting, minted.Valence);
    }

    [Fact]
    public async Task Same_polarity_near_duplicate_proposal_is_skipped()
    {
        var affirming = Insight("standing", Valence.Affirming,
            "Redis connection pooling stays stable under sustained production load across restarts");
        var store = new SeededDuplicateStore(affirming);
        await store.StoreAsync(Echoed("src1"));
        // Same (affirming) stance ⇒ a genuine duplicate ⇒ skipped (no contradiction to preserve).
        var json =
            """[{"content":"Redis connection pooling stays perfectly stable under sustained production load","type":"insight","valence":"affirming","tags":["redis"]}]""";
        using var enrichment = Enrichment(json);
        var engine = Engine(store, enrichment);

        var result = await engine.ReflectAsync(Repo);

        Assert.Equal(0, result.MemoriesCreated);
        Assert.Empty(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
    }

    // ─── 7. Anti-laundering provenance stamp ──────────────────────────────

    [Theory]
    [InlineData(MemoryProvenance.Pack)]
    [InlineData(MemoryProvenance.Intake)]
    public async Task Import_provenance_contributor_stamps_minted_memory_with_that_provenance_not_reflection(
        MemoryProvenance importProvenance)
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Echoed("poison", provenance: importProvenance));
        using var enrichment = Enrichment(OneInsightJson);
        var engine = Engine(store, enrichment);

        var result = await engine.ReflectAsync(Repo);

        Assert.Equal(1, result.MemoriesCreated);
        var minted = Assert.Single(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
        // The laundering is defeated: the mint inherits the least-trusted contributor's provenance,
        // so MemoryTrust keeps demoting it — it never reads as the trusted Reflection stamp.
        Assert.Equal(importProvenance, minted.Provenance);
        Assert.True(MemoryTrust.Factor(minted) <= 0.5,
            $"laundered reflection trust ({MemoryTrust.Factor(minted)}) must not exceed the import floor");
        Assert.Equal(importProvenance, result.Candidates.Single().Provenance);
    }

    // ─── 8. Coverage cursor advances and gates re-proposal ────────────────

    [Fact]
    public async Task Live_run_advances_cursor_and_an_unchanged_second_run_mints_nothing()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(Echoed("src1"));
        using var enrichment = Enrichment(OneInsightJson);
        var engine = Engine(store, enrichment);

        var first = await engine.ReflectAsync(Repo);
        Assert.Equal(1, first.MemoriesCreated);
        Assert.NotNull(await store.GetLastReflectedAtAsync(Repo)); // cursor set by the live run

        // Nothing changed → the same residue now sits behind the cursor → no re-proposal, no new mint.
        var second = await engine.ReflectAsync(Repo);
        Assert.Equal(0, second.MemoriesCreated);
        Assert.Single(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
    }

    // ─── 11. Loose-end residue arm (integration through the engine) ───────

    [Fact]
    public async Task LooseEnds_source_mints_from_a_done_unpromoted_end_with_reflection_provenance()
    {
        var store = new InMemoryEidetStore();
        var ends = new InMemoryLooseEndStore();
        await ends.StoreAsync(End("done", LooseEndState.Resolved, ResolutionKind.Done));
        using var enrichment = Enrichment(OneInsightJson);
        var engine = Engine(store, enrichment, looseEnds: ends);

        var result = await engine.ReflectAsync(Repo, source: ReflectionSource.LooseEnds);

        Assert.Equal(1, result.MemoriesCreated);
        var minted = Assert.Single(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
        // Loose ends are first-party parked work with no provenance to launder: they inform content
        // but never the trust stamp, so a loose-end-only reflection is trusted Reflection with no lineage.
        Assert.Equal(MemoryProvenance.Reflection, minted.Provenance);
        Assert.Empty(minted.DerivedFrom);
    }

    [Theory]
    [InlineData(LooseEndState.Resolved, ResolutionKind.Dropped)]
    [InlineData(LooseEndState.Resolved, ResolutionKind.Superseded)]
    [InlineData(LooseEndState.Open, null)]
    public async Task LooseEnds_source_ignores_non_done_ends(LooseEndState state, ResolutionKind? resolution)
    {
        var store = new InMemoryEidetStore();
        var ends = new InMemoryLooseEndStore();
        await ends.StoreAsync(End("x", state, resolution));
        using var enrichment = Enrichment(OneInsightJson);
        var engine = Engine(store, enrichment, looseEnds: ends);

        var result = await engine.ReflectAsync(Repo, source: ReflectionSource.LooseEnds);

        Assert.Equal(0, result.MemoriesCreated);
        Assert.Empty(await store.BrowseAsync(Repo, 0, 50, MemoryType.Insight));
    }

    [Fact]
    public async Task ListResolvedUnpromoted_surfaces_only_done_and_unpromoted_ends()
    {
        var ends = new InMemoryLooseEndStore();
        await ends.StoreAsync(End("done", LooseEndState.Resolved, ResolutionKind.Done));
        await ends.StoreAsync(End("dropped", LooseEndState.Resolved, ResolutionKind.Dropped));
        await ends.StoreAsync(End("promoted", LooseEndState.Resolved, ResolutionKind.Promoted, promotedTo: "memories/x"));
        await ends.StoreAsync(End("superseded", LooseEndState.Resolved, ResolutionKind.Superseded));
        await ends.StoreAsync(End("open", LooseEndState.Open, null));
        await ends.StoreAsync(End("done-promoted", LooseEndState.Resolved, ResolutionKind.Done, promotedTo: "memories/y"));

        var got = await ends.ListResolvedUnpromotedAsync(Repo, since: null, max: 50);

        Assert.Equal(new[] { $"looseends/{Repo}/done" }, got.Select(e => e.Id).ToArray());
    }

    /// <summary>
    /// <see cref="InMemoryEidetStore"/> whose <see cref="FindDuplicateAsync"/> always returns a seeded
    /// entry — drives the engine's duplicate/valence-conflict branch (mirrors <c>BoostStore</c>).
    /// </summary>
    private sealed class SeededDuplicateStore(MemoryEntry duplicate) : InMemoryEidetStore
    {
        public override Task<MemoryEntry?> FindDuplicateAsync(
            string repoId, string content, float threshold, CancellationToken ct = default) =>
            Task.FromResult<MemoryEntry?>(duplicate);
    }
}
