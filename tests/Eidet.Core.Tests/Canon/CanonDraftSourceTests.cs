using Eidet.Core.Canon;
using Eidet.Core.Canon.Sources;
using Eidet.Core.Domain;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Canon;

/// <summary>
/// Contract tests for the two P1 <see cref="ICanonDraftSource"/> implementations:
/// <see cref="UbiquitousLanguageDraftSource"/> (parses a repo's UBIQUITOUS_LANGUAGE.md glossary tables)
/// and <see cref="EntityAggregationDraftSource"/> (proposes a Term per entity cited by ≥2 non-Observation
/// memories, defined by the highest-importance citing memory).
/// </summary>
public class CanonDraftSourceTests
{
    // ─── UbiquitousLanguageDraftSource ──────────────────────────────────

    private const string UlFixture =
        """
        # Ubiquitous Language

        ## Core terms

        | Term | Definition | Aliases to avoid |
        |------|------------|------------------|
        | **Memory** | A stored unit of knowledge | recollection |
        | **Loose End** | Parked open work to resolve later | todo |

        ## Example dialogue

        | **IgnoredDialogue** | This row is under a skipped section | n/a |

        ## More terms

        | **Portal** | The per-repo generated web UI state view | dashboard |

        ## Flagged ambiguities

        | **IgnoredAmbiguity** | Under the flagged-ambiguities skip section | n/a |
        """;

    [Fact]
    public async Task UbiquitousLanguage_ParsesTermRows_SkipsNarrativeSections_AndHeaderRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eidet-canon-ul-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "UBIQUITOUS_LANGUAGE.md"), UlFixture);

            var source = new UbiquitousLanguageDraftSource();
            var ctx = new CanonProposalContext(RepoIdNormalizer.Normalize(dir), dir);

            Assert.True(source.AppliesTo(ctx));

            var candidates = await CollectAsync(source, ctx);

            // Exactly the three real term rows — one per non-skipped section, header/separator rows dropped.
            Assert.Equal(3, candidates.Count);
            var bySlug = candidates.ToDictionary(c => c.Slug);
            Assert.Equal(new[] { "loose-end", "memory", "portal" }, bySlug.Keys.OrderBy(k => k).ToArray());

            // Every candidate is a Term with no members (UL terms are authored, not memory-derived).
            Assert.All(candidates, c => Assert.Equal(CanonKind.Term, c.Kind));
            Assert.All(candidates, c => Assert.Empty(c.MemberIds));
            Assert.All(candidates, c => Assert.False(string.IsNullOrEmpty(c.Fingerprint)));

            var memory = bySlug["memory"];
            Assert.Equal("Memory", memory.Title);
            Assert.Contains("A stored unit of knowledge", memory.ProposedContent);
            Assert.Contains("Core terms", memory.ProposedContent);           // section attribution
            Assert.Contains("UBIQUITOUS_LANGUAGE.md", memory.ProposedContent);

            // Rows under "Example dialogue" and "Flagged ambiguities" never surface.
            Assert.DoesNotContain(candidates, c => c.Title.Contains("Ignored"));
            // The literal header cell "Term" is not mistaken for a term.
            Assert.DoesNotContain(candidates, c => c.Title == "Term");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UbiquitousLanguage_AppliesFalse_WhenFileAbsent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eidet-canon-ul-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);   // dir exists, but no UBIQUITOUS_LANGUAGE.md in it
        try
        {
            var source = new UbiquitousLanguageDraftSource();
            var ctx = new CanonProposalContext(RepoIdNormalizer.Normalize(dir), dir);
            Assert.False(source.AppliesTo(ctx));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─── EntityAggregationDraftSource ───────────────────────────────────

    [Fact]
    public async Task EntityAggregation_ProposesTerm_PerEntityCitedByTwoPlusNonObservations()
    {
        var store = new InMemoryEidetStore();

        // "RavenDB": two citing Insights — the higher-importance one (A) defines it.
        await store.StoreAsync(Insight("aaa", entity: "RavenDB", importance: 0.9f,
            oneLiner: "embedded document DB with vector and full-text search",
            content: "RavenDB backs hybrid search"));
        await store.StoreAsync(Insight("bbb", entity: "RavenDB", importance: 0.5f,
            oneLiner: "a lesser line about the store",
            content: "RavenDB stores memories"));

        // "Ollama": single citation → below the 2-citation floor, no draft.
        await store.StoreAsync(Insight("ccc", entity: "Ollama", importance: 0.7f,
            oneLiner: "local enrichment backend", content: "Ollama runs enrichment"));

        // An Observation citing "RavenDB" — Observations are excluded, so it must not raise the count
        // (nor become the definition despite its high importance).
        await store.StoreAsync(new MemoryEntry
        {
            Id = "memories/repo-a/observation/ddd",
            RepoId = "repo-a",
            Type = MemoryType.Observation,
            Content = "session residue about RavenDB",
            Entities = ["RavenDB"],
            OneLiner = "residue that must not define the term",
            Importance = 0.99f,
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
        });

        // "Spectre": two citing Insights where the top (E) has NO OneLiner — exercises the content fallback.
        await store.StoreAsync(Insight("eee", entity: "Spectre", importance: 0.7f,
            oneLiner: null, content: "Spectre.Console renders the TUI tables and CLI output layouts"));
        await store.StoreAsync(Insight("fff", entity: "Spectre", importance: 0.4f,
            oneLiner: "spectre lesser one-liner", content: "Spectre is used somewhere"));

        var source = new EntityAggregationDraftSource(store);
        var ctx = new CanonProposalContext("repo-a", "repo-a");
        Assert.True(source.AppliesTo(ctx));

        var candidates = await CollectAsync(source, ctx);

        // Only the two multi-cited entities — never "Ollama" (single) or an Observation-only entity.
        Assert.Equal(2, candidates.Count);
        Assert.DoesNotContain(candidates, c => c.Slug == "ollama");

        var raven = candidates.Single(c => c.Slug == "ravendb");
        Assert.Equal("RavenDB", raven.Title);
        Assert.Equal(CanonKind.Term, raven.Kind);
        // Definition comes from the HIGHEST-importance citing memory (A, 0.9), not B.
        Assert.Contains("embedded document DB with vector", raven.ProposedContent);
        Assert.DoesNotContain("lesser line", raven.ProposedContent);
        // Members are exactly the two citing Insights, ordinal-ordered; the Observation is not among them.
        Assert.Equal(
            new[] { "memories/repo-a/insight/aaa", "memories/repo-a/insight/bbb" },
            raven.MemberIds);
        Assert.False(string.IsNullOrEmpty(raven.Fingerprint));

        // Spectre's top citing memory (E) has no OneLiner → definition falls back to its content.
        var spectre = candidates.Single(c => c.Slug == "spectre");
        Assert.Contains("Spectre.Console renders the TUI", spectre.ProposedContent);
        Assert.DoesNotContain("lesser one-liner", spectre.ProposedContent);
    }

    [Fact]
    public async Task EntityAggregation_ExcludesCanonPages_FromMemberPool()
    {
        var store = new InMemoryEidetStore();

        await store.StoreAsync(Insight("aaa", entity: "RavenDB", importance: 0.9f,
            oneLiner: "embedded document DB with vector and full-text search",
            content: "RavenDB backs hybrid search"));
        await store.StoreAsync(Insight("bbb", entity: "RavenDB", importance: 0.5f,
            oneLiner: "a lesser line about the store",
            content: "RavenDB stores memories"));

        // An approved canon page that itself mentions the entity: despite being the highest-importance
        // citer, it must never re-enter as a member of its own term's next draft (self-referential
        // DerivedFrom) nor become the definition — the guard's third read path.
        await store.StoreAsync(new MemoryEntry
        {
            Id = "memories/repo-a/insight/canon-ravendb",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "RavenDB: embedded document DB (canon page)",
            Entities = ["RavenDB"],
            Tags = ["ravendb", "canon:term:ravendb"],
            OneLiner = "the canon page one-liner",
            Importance = 0.99f,
            CreatedAt = DateTime.UtcNow,
            Validity = new Validity { ValidFrom = DateTime.UtcNow },
            IsLatest = true,
        });

        var source = new EntityAggregationDraftSource(store);
        var candidates = await CollectAsync(source, new CanonProposalContext("repo-a", "repo-a"));

        var raven = candidates.Single(c => c.Slug == "ravendb");
        Assert.DoesNotContain("memories/repo-a/insight/canon-ravendb", raven.MemberIds);
        Assert.DoesNotContain("canon page one-liner", raven.ProposedContent);
        Assert.Equal(
            new[] { "memories/repo-a/insight/aaa", "memories/repo-a/insight/bbb" },
            raven.MemberIds);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static async Task<List<CanonDraftCandidate>> CollectAsync(
        ICanonDraftSource source, CanonProposalContext ctx)
    {
        var list = new List<CanonDraftCandidate>();
        await foreach (var c in source.ProposeAsync(ctx))
            list.Add(c);
        return list;
    }

    private static MemoryEntry Insight(
        string idSuffix, string entity, float importance, string? oneLiner, string content) => new()
    {
        Id = $"memories/repo-a/insight/{idSuffix}",
        RepoId = "repo-a",
        Type = MemoryType.Insight,
        Content = content,
        Entities = [entity],
        OneLiner = oneLiner,
        Importance = importance,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
    };
}
