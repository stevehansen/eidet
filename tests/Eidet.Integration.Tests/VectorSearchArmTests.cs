using Eidet.Core.Domain;
using Eidet.Core.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace Eidet.Integration.Tests;

/// <summary>
/// The semantic arm against a RavenDB that really has an embeddings task.
///
/// This is the coverage whose absence let the arm ship broken: every other suite runs on a store with no
/// embeddings task, where <see cref="RavenEidetStore.VectorSearchAsync"/> correctly returns nothing — so a
/// query the server rejected outright was indistinguishable from a healthy no-embeddings install, and
/// 1,500 tests stayed green while recall's semantic arm, the abstraction arm, the vector write-gate and
/// dedup's semantic pass were all dead. The bug was a missing AND between the repo filter and the vector
/// clause; the fix is one operator, and only a live task can prove it.
/// </summary>
public class VectorSearchArmTests : IAsyncLifetime
{
    private const string Repo = "vector-arm-repo";

    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "eidet-vector-arm", Guid.NewGuid().ToString("N")[..8]);

    private IDocumentStore? _raven;
    private RavenEidetStore _store = null!;

    /// <summary>
    /// False when this machine cannot run an embedded server with an embeddings task at all — CI runners
    /// skip the whole integration suite for the same reason. Distinct from "embeddings are available but
    /// produced nothing", which is the defect these tests exist to catch and which fails loudly below.
    /// </summary>
    private bool _available;

    public Task InitializeAsync()
    {
        try
        {
            _raven = DocumentStoreFactory.CreateEmbedded(_dataDir, $"VecTest_{Guid.NewGuid():N}"[..20]);
            IndexCreation.CreateIndexes(typeof(Eidet.Core.Indexes.Memories_Search).Assembly, _raven);
            _available = DatabaseProvisioner.EnsureEmbeddingsConfigured(_raven) is null;
            _store = new RavenEidetStore(_raven);
        }
        catch
        {
            _available = false;
        }

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _raven?.Dispose(); } catch { /* never came up */ }
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* temp dir */ }
        return Task.CompletedTask;
    }

    private static MemoryEntry Insight(string id, string content) => new()
    {
        Id = $"memories/{Repo}/insight/{id}",
        RepoId = Repo,
        Type = MemoryType.Insight,
        Content = content,
        Importance = 0.6f,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
    };

    [SkippableFact]
    public async Task Semantic_arm_returns_a_paraphrase_the_lexical_arm_cannot_match()
    {
        Skip.IfNot(_available, "Embedded RavenDB with an embeddings task not available");

        await _store.StoreAsync(Insight("gate", "The deployment gate verifies release signatures before promotion"));
        await _store.StoreAsync(Insight("avatar", "Frontend avatar images are cached in browser local storage"));

        // Shares almost no vocabulary with the stored wording, so a hit here can only be semantic.
        const string paraphrase = "artifact signing is checked prior to shipping a build";

        var hits = await WaitForVectorHitsAsync(paraphrase);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Id.EndsWith("/gate", StringComparison.Ordinal));

        // The point of the arm: the lexical arm cannot find this, which is what the vector probe adds.
        var lexical = await _store.FullTextSearchAsync(
            [Repo], new MemoryQuery { Text = paraphrase, Limit = 10 });
        Assert.DoesNotContain(lexical, h => h.Id.EndsWith("/gate", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Semantic_arm_honours_a_page_wider_than_the_default_candidate_count()
    {
        Skip.IfNot(_available, "Embedded RavenDB with an embeddings task not available");

        // The HNSW candidate count is also a hard ceiling on rows returned, so it has to track the
        // requested page. Pinned at 30, a caller asking for 60 silently got 30 — which quietly
        // truncated every recall with a limit above 30, not just the merge guard's ranking.
        for (var i = 0; i < 60; i++)
            await _store.StoreAsync(Insight($"m{i:D2}", $"Release signature verification step number {i} in the deployment gate"));

        // Wait for enough of them to be embedded that a 30-row ceiling would be visible as a ceiling.
        await WaitForVectorHitsAsync("signature verification in the deployment gate", minHits: 40);

        var wide = await _store.VectorSearchAsync(
            [Repo], new MemoryQuery { Text = "signature verification in the deployment gate", Limit = 60 });

        Assert.True(wide.Count > 30, $"expected more than 30 rows for a limit of 60, got {wide.Count}");
    }

    /// <summary>
    /// Embedding generation is a background task, so the first probe after a write usually finds nothing —
    /// and the probe right after that finds only the handful embedded so far. Polls until at least
    /// <paramref name="minHits"/> rows are available; a timeout fails rather than skips, because a silent
    /// skip is the exact failure mode this class exists to prevent.
    /// </summary>
    private async Task<List<MemoryEntry>> WaitForVectorHitsAsync(string text, int minHits = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        List<MemoryEntry> hits = [];
        while (DateTime.UtcNow < deadline)
        {
            hits = await _store.VectorSearchAsync([Repo], new MemoryQuery { Text = text, Limit = 100 });
            if (hits.Count >= minHits) return hits;
            await Task.Delay(500);
        }

        Assert.Fail($"the semantic arm produced {hits.Count} hits within 60s, needed {minHits} — embeddings " +
                    "never generated, or the vector query is malformed and the store's catch is reporting " +
                    "it as 'no embeddings'");
        return hits;
    }
}
