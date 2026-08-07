using Eidet.Core.Domain;
using Eidet.Core.Indexes;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace Eidet.Core.Storage;

public class RavenEidetStore : IEidetStore
{
    private const string EmbeddingsTaskId = "memory-embeddings";

    /// <summary>Candidate over-fetch factor for <see cref="GetTopScoredAsync"/> — see the note there.</summary>
    private const int PoolOverfetch = 5;

    /// <summary>Absolute ceiling on that over-fetch, so a large caller limit can't pull the corpus.</summary>
    private const int MaxPool = 500;

    /// <summary>
    /// Cosine floor for the semantic arms. Surfaced as a property (not a constant) because
    /// <c>memory.vectorSimilarityMinimum</c> is a documented, user-settable knob — it existed in
    /// config and in the CLI while nothing ever read it, so tuning it silently did nothing.
    /// </summary>
    public float VectorSimilarityMinimum { get; set; } = 0.70f;

    /// <summary>Candidate pool for the exact-content fallback — see the note in FindDuplicateCoreAsync.</summary>
    private const int ExactMatchPool = 128;
    private readonly IDocumentStore _store;

    /// <param name="config">
    /// Optional so the many call sites that only read or write documents stay untouched. Supplied by
    /// the recall-serving entry points (MCP, serve, recall, context), which are the only ones whose
    /// behavior the tuning knobs change.
    /// </param>
    public RavenEidetStore(IDocumentStore store, Configuration.EidetConfig? config = null)
    {
        _store = store;
        if (config is not null)
            VectorSimilarityMinimum = config.Memory.VectorSimilarityMinimum;
    }

    public async Task<MemoryEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        return await session.LoadAsync<MemoryEntry>(id, ct);
    }

    public async Task<IReadOnlyDictionary<string, MemoryEntry?>> GetManyAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var resolved = new Dictionary<string, MemoryEntry?>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return resolved;

        using var session = _store.OpenAsyncSession();
        // One round trip for the whole set; RavenDB returns a null value for an id that does not exist,
        // which is exactly the distinction the caller needs.
        foreach (var (id, entry) in await session.LoadAsync<MemoryEntry>(ids, ct))
            resolved[id] = entry;
        return resolved;
    }

    public async Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        if (entry.CreatedAt == default)
            entry.CreatedAt = DateTime.UtcNow;

        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(entry, entry.Id, ct);
        await session.SaveChangesAsync(ct);
        return entry.Id;
    }

    public async Task UpdateAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(entry, entry.Id, ct);
        await session.SaveChangesAsync(ct);
    }

    public async Task<bool> ForgetAsync(string id, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var entry = await session.LoadAsync<MemoryEntry>(id, ct);
        if (entry is null) return false;

        entry.Validity.ValidUntil = DateTime.UtcNow;
        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task PatchAccessAsync(string entryId, DateTime lastAccessedAt, double? lexShare = null, CancellationToken ct = default)
    {
        // Patch only AccessCount + LastAccessedAt (+ LastLexShare when provided) — never load or replace
        // the full entry. Keeps this path narrow so it cannot accidentally clobber other fields on a race.
        // LastLexShare is set only when a share is supplied so a non-attributed bump leaves it untouched.
        var patchOp = new Raven.Client.Documents.Operations.PatchOperation(
            entryId,
            changeVector: null,
            new Raven.Client.Documents.Operations.PatchRequest
            {
                Script = "this.AccessCount = (this.AccessCount || 0) + 1; this.LastAccessedAt = args.At;"
                    + " if (args.LexShare !== null) this.LastLexShare = args.LexShare;",
                Values = { { "At", lastAccessedAt }, { "LexShare", lexShare } },
            });
        try { await _store.Operations.SendAsync(patchOp, token: ct); }
        catch { /* Non-critical — access tracking is best-effort */ }
    }

    public async Task<double?> GetRepoAlphaAsync(string repoId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var usage = await session.LoadAsync<RepoUsage>(RepoUsage.MakeId(repoId), ct);
        return usage?.AlphaLex;
    }

    public async Task UpdateRepoAlphaAsync(string repoId, AlphaEwmaUpdate update, CancellationToken ct = default)
    {
        // Patch ONLY AlphaLex + AlphaSamples on the usage anchor so we never clobber UsageTracker's
        // time series or OriginalPath. The EWMA is computed SERVER-SIDE from the document's current
        // AlphaLex (the patch reads this.AlphaLex at apply time), so two concurrent feedbacks each fold
        // into the latest value — no read-compute-write race that a C#-side computed value would have.
        // patchIfMissing upserts the doc (folding from Fallback) the first time alpha is learned for a
        // repo with no usage anchor yet. Surfaces failures (no swallow) — the caller treats alpha tuning
        // as best-effort, but a silent storage fault here would hide a real regression in learning.
        var normalized = RepoIdNormalizer.Normalize(repoId);
        var docId = RepoUsage.MakeId(repoId);
        const string ewma =
            "var cur = (this.AlphaLex === null || this.AlphaLex === undefined) ? args.Fallback : this.AlphaLex;"
            + " var next = (1 - args.Lambda) * cur + args.Lambda * args.Target;"
            + " if (next < args.Min) next = args.Min; if (next > args.Max) next = args.Max;"
            + " this.AlphaLex = next;";
        var patchOp = new Raven.Client.Documents.Operations.PatchOperation(
            docId,
            changeVector: null,
            patch: new Raven.Client.Documents.Operations.PatchRequest
            {
                Script = ewma + " this.AlphaSamples = (this.AlphaSamples || 0) + 1;",
                Values =
                {
                    { "Target", update.Target }, { "Lambda", update.Lambda },
                    { "Min", update.Min }, { "Max", update.Max }, { "Fallback", update.Fallback },
                },
            },
            patchIfMissing: new Raven.Client.Documents.Operations.PatchRequest
            {
                Script = "this.Id = args.Id; this.RepoId = args.RepoId; this.CreatedAt = args.Now;"
                    + " " + ewma + " this.AlphaSamples = 1;",
                Values =
                {
                    { "Id", docId }, { "RepoId", normalized }, { "Now", DateTime.UtcNow },
                    { "Target", update.Target }, { "Lambda", update.Lambda },
                    { "Min", update.Min }, { "Max", update.Max }, { "Fallback", update.Fallback },
                },
            });
        await _store.Operations.SendAsync(patchOp, token: ct);
    }

    public async Task<DateTime?> GetLastReflectedAtAsync(string repoId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var usage = await session.LoadAsync<RepoUsage>(RepoUsage.MakeId(repoId), ct);
        return usage?.LastReflectedAt;
    }

    public async Task SetLastReflectedAtAsync(string repoId, DateTime whenUtc, CancellationToken ct = default)
    {
        // Patch ONLY LastReflectedAt on the usage anchor (same discipline as UpdateRepoAlphaAsync) so
        // the reflection cursor never clobbers UsageTracker's time series, OriginalPath, or the learned
        // alpha. patchIfMissing upserts the anchor the first time a repo is reflected with no usage doc yet.
        var normalized = RepoIdNormalizer.Normalize(repoId);
        var docId = RepoUsage.MakeId(repoId);
        var patchOp = new Raven.Client.Documents.Operations.PatchOperation(
            docId,
            changeVector: null,
            patch: new Raven.Client.Documents.Operations.PatchRequest
            {
                // Monotonic (forward-only) guard — the cursor is a coverage watermark, so a concurrent
                // maintenance run with an older `now` must never pull it backward and re-open a window.
                // ISO-8601 UTC strings compare chronologically, so the string comparison is correct here.
                Script = "if (this.LastReflectedAt == null || args.When > this.LastReflectedAt) this.LastReflectedAt = args.When;",
                Values = { { "When", whenUtc } },
            },
            patchIfMissing: new Raven.Client.Documents.Operations.PatchRequest
            {
                Script = "this.Id = args.Id; this.RepoId = args.RepoId; this.CreatedAt = args.Now; this.LastReflectedAt = args.When;",
                Values = { { "Id", docId }, { "RepoId", normalized }, { "Now", DateTime.UtcNow }, { "When", whenUtc } },
            });
        await _store.Operations.SendAsync(patchOp, token: ct);
    }

    public async Task<string?> GetGitIntakeWatermarkAsync(string repoId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var usage = await session.LoadAsync<RepoUsage>(RepoUsage.MakeId(repoId), ct);
        return usage?.GitIntakeLastSha;
    }

    public async Task SetGitIntakeWatermarkAsync(string repoId, string sha, CancellationToken ct = default)
    {
        // Patch ONLY GitIntakeLastSha on the usage anchor (same discipline as SetLastReflectedAtAsync).
        // Last-write-wins: SHAs carry no order, so there is no monotonic guard to apply.
        var normalized = RepoIdNormalizer.Normalize(repoId);
        var docId = RepoUsage.MakeId(repoId);
        var patchOp = new Raven.Client.Documents.Operations.PatchOperation(
            docId,
            changeVector: null,
            patch: new Raven.Client.Documents.Operations.PatchRequest
            {
                Script = "this.GitIntakeLastSha = args.Sha;",
                Values = { { "Sha", sha } },
            },
            patchIfMissing: new Raven.Client.Documents.Operations.PatchRequest
            {
                Script = "this.Id = args.Id; this.RepoId = args.RepoId; this.CreatedAt = args.Now; this.GitIntakeLastSha = args.Sha;",
                Values = { { "Id", docId }, { "RepoId", normalized }, { "Now", DateTime.UtcNow }, { "Sha", sha } },
            });
        await _store.Operations.SendAsync(patchOp, token: ct);
    }

    public async Task<List<MemoryEntry>> FullTextSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        (await SearchScoredAsync(SearchArm.Lexical, repoIds, query, ct)).Select(h => h.Entry).ToList();

    public async Task<List<MemoryEntry>> VectorSearchAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        (await SearchScoredAsync(SearchArm.Vector, repoIds, query, ct)).Select(h => h.Entry).ToList();

    public Task<IReadOnlyList<ScoredHit>> SearchScoredAsync(
        SearchArm arm, IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct = default) =>
        arm switch
        {
            SearchArm.Lexical => LexicalScoredAsync(repoIds, query, ct),
            SearchArm.Abstraction => VectorScoredAsync(repoIds, query, "AbstractionVector", ct),
            _ => VectorScoredAsync(repoIds, query, "SearchVector", ct),
        };

    private async Task<IReadOnlyList<ScoredHit>> LexicalScoredAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, CancellationToken ct)
    {
        using var session = _store.OpenAsyncSession();
        // WhereIn MUST come before Search to ensure AND semantics.
        // RavenDB's Search uses OR by default, so placing it after Where
        // ensures the repo filter is applied as an AND condition.
        var documentQuery = session.Advanced
            .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
            .WhereIn("RepoId", repoIds)
            .AndAlso()
            .Search("SearchText", query.Text)
            .OrderByScore();

        documentQuery = ApplyFilters(documentQuery, query);
        var hits = await documentQuery.Take(query.Limit * 2).ToListAsync(ct); // Over-fetch 2× for merge quality
        return ToScoredHits(session, hits);
    }

    /// <summary>
    /// One semantic arm. <paramref name="vectorField"/> selects WHAT was embedded — the composite
    /// entry text ("SearchVector") or the abstraction alone ("AbstractionVector"); the query text is
    /// embedded by the same task either way, so the two arms' similarities are on one scale and
    /// fusion can normalize them independently without a per-arm calibration.
    /// </summary>
    private async Task<IReadOnlyList<ScoredHit>> VectorScoredAsync(
        IReadOnlyList<string> repoIds, MemoryQuery query, string vectorField, CancellationToken ct)
    {
        try
        {
            using var session = _store.OpenAsyncSession();
            var documentQuery = session.Advanced
                .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
                .WhereIn("RepoId", repoIds)
                .VectorSearch(
                    field => field.WithField(vectorField),
                    searchTerm => searchTerm.ByText(query.Text, EmbeddingsTaskId),
                    minimumSimilarity: VectorSimilarityMinimum,
                    numberOfCandidates: 30);

            documentQuery = ApplyFilters(documentQuery, query);
            var hits = await documentQuery.Take(query.Limit).ToListAsync(ct);
            return ToScoredHits(session, hits);
        }
        catch
        {
            return []; // Vector search may fail if embeddings not configured
        }
    }

    /// <summary>
    /// Reads each hit's raw relevance from the already-materialized session metadata
    /// (<c>@index-score</c>). Falls back to rank-decay (<c>1, 1/2, 1/3, …</c>) for any hit whose
    /// score the backend doesn't surface (e.g. the vector arm), so an arm never throws over a
    /// missing score. A missing key is the expected case and is handled without an exception (no
    /// per-hit throw cost); the narrow catch covers only a present-but-unconvertible value.
    /// </summary>
    private static IReadOnlyList<ScoredHit> ToScoredHits(IAsyncDocumentSession session, List<MemoryEntry> hits)
    {
        var scored = new List<ScoredHit>(hits.Count);
        for (var rank = 0; rank < hits.Count; rank++)
        {
            var entry = hits[rank];
            var rankDecay = 1.0 / (rank + 1);
            var metadata = session.Advanced.GetMetadataFor(entry);
            double score;
            if (!metadata.ContainsKey("@index-score"))
                score = rankDecay;
            else
                try { score = metadata.GetDouble("@index-score"); }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
                { score = rankDecay; }
            scored.Add(new ScoredHit(entry, score));
        }
        return scored;
    }

    public Task<MemoryEntry?> FindDuplicateAsync(
        string repoId, string content, float threshold, CancellationToken ct = default) =>
        FindDuplicateCoreAsync(repoId, null, content, threshold, ct);

    public Task<MemoryEntry?> FindDuplicateOfTypeAsync(
        string repoId, MemoryType type, string content, float threshold, CancellationToken ct = default) =>
        FindDuplicateCoreAsync(repoId, type, content, threshold, ct);

    private async Task<MemoryEntry?> FindDuplicateCoreAsync(
        string repoId, MemoryType? type, string content, float threshold, CancellationToken ct)
    {
        // Strategy 1: Vector similarity
        try
        {
            using var session = _store.OpenAsyncSession();
            var q = session.Advanced
                .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
                .WhereEquals("RepoId", repoId)
                .AndAlso()
                .WhereEquals("ValidUntil", (DateTime?)null);
            if (type is { } t)
                q = q.AndAlso().WhereEquals("Type", t);

            var results = await q
                .VectorSearch(
                    field => field.WithField("SearchVector"),
                    searchTerm => searchTerm.ByText(content, EmbeddingsTaskId),
                    minimumSimilarity: threshold,
                    numberOfCandidates: 10)
                .Take(1)
                .ToListAsync(ct);

            if (results.Count > 0)
                return results[0];
        }
        catch { /* embeddings unavailable or index stale — Strategy 2 is the safety net */ }

        // Strategy 2: exact-content match over a lexical candidate pool. This is the ONLY guard that
        // survives an embeddings outage, so its pool must not be starved by the very condition it
        // exists to detect: with a narrow Take, a content that already has many copies fills every
        // slot with equally-scoring siblings and the exact match can fall outside the window, so the
        // gate reports "no duplicate" precisely when duplicates are worst and mints another. The pool
        // is therefore sized well above any plausible duplicate cluster.
        try
        {
            var searchSnippet = content.Length > 80 ? content[..80] : content;
            using var session = _store.OpenAsyncSession();
            var q = session.Advanced
                .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
                .WhereEquals("RepoId", repoId)
                .AndAlso()
                .WhereEquals("ValidUntil", (DateTime?)null);
            if (type is { } t2)
                q = q.AndAlso().WhereEquals("Type", t2);

            var candidates = await q
                .AndAlso()
                .Search("Content", searchSnippet)
                .Take(ExactMatchPool)
                .ToListAsync(ct);

            return candidates.FirstOrDefault(c =>
                string.Equals(c.Content.Trim(), content.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<MemoryEntry>> FindNearDuplicatesAsync(
        string repoId, MemoryEntry entry, float minSimilarity, int max, CancellationToken ct = default)
    {
        try
        {
            using var session = _store.OpenAsyncSession();
            var results = await session.Advanced
                .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
                .WhereEquals("RepoId", repoId)
                .AndAlso()
                .WhereEquals("ValidUntil", (DateTime?)null)
                .AndAlso()
                .WhereEquals("Type", entry.Type)
                .VectorSearch(
                    field => field.WithField("SearchVector"),
                    searchTerm => searchTerm.ByText(entry.Content, EmbeddingsTaskId),
                    minimumSimilarity: minSimilarity,
                    numberOfCandidates: 30)
                .Take(max)
                .ToListAsync(ct);

            // Exclude the entry itself; IsLatest is not in the search index so filter client-side.
            return results.Where(e => e.IsLatest && e.Id != entry.Id).ToList();
        }
        catch
        {
            return []; // Vector search may fail if embeddings not configured
        }
    }

    public async Task<IReadOnlyList<MemoryEntry>> FindByEntitiesAsync(
        IReadOnlyList<string> repoIds, IReadOnlyCollection<string> entities,
        IReadOnlyCollection<string> excludeIds, int max, CancellationToken ct = default)
    {
        if (entities.Count == 0 || max <= 0) return [];

        using var session = _store.OpenAsyncSession();
        // Over-fetch by the exclusion count so entries we are about to drop can't starve the result:
        // the cues come FROM the current pool, so the pool itself is the likeliest set of matches.
        var candidates = await session.Advanced
            .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
            .WhereIn("RepoId", repoIds)
            .AndAlso()
            .WhereEquals("ValidUntil", (DateTime?)null)
            .AndAlso()
            // Lower-cased to match the index projection — see Memories_Search. WhereIn over an array
            // field matches any element.
            .WhereIn("Entities", entities.Select(e => e.ToLowerInvariant()))
            .Take(max + excludeIds.Count)
            .ToListAsync(ct);

        var excluded = new HashSet<string>(excludeIds, StringComparer.OrdinalIgnoreCase);
        // IsLatest is not in the search index (same reason as FindNearDuplicatesAsync) — filter here.
        return candidates.Where(e => e.IsLatest && !excluded.Contains(e.Id)).Take(max).ToList();
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetInvalidatedAsync(
        string repoId, int max, CancellationToken ct = default)
    {
        // Every soft-deleted memory carries a set ValidUntil — forget stamps it, and both supersession
        // paths (store-supersede, content edit) stamp it alongside IsLatest=false — so this single
        // indexed predicate captures the forgotten AND superseded sets the auditor must probe.
        using var session = _store.OpenAsyncSession();
        var results = await session.Advanced
            .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
            .WhereEquals("RepoId", repoId)
            .AndAlso()
            .WhereNotEquals("ValidUntil", (DateTime?)null)
            .Take(max)
            .ToListAsync(ct);
        return results;
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetUnprovenancedAsync(
        string repoId, IReadOnlyCollection<string> repairableSources, int limit, CancellationToken ct = default)
    {
        if (repairableSources.Count == 0) return [];

        // A collection auto-index, not Memories/Search: Source is not a field of that index, and adding
        // one would re-map — and therefore re-embed — the entire corpus, since its SearchVector is
        // computed inside the map.
        using var session = _store.OpenAsyncSession();
        return await session.Advanced.AsyncDocumentQuery<MemoryEntry>()
            .WhereEquals("RepoId", repoId)
            .AndAlso()
            .WhereEquals("Validity.ValidUntil", (object?)null)
            .AndAlso()
            .WhereEquals("IsLatest", true)
            .AndAlso()
            .WhereIn("Source", repairableSources.Cast<object>())
            .AndAlso()
            // The literal "Unknown" OR no such property at all. A document written before the field
            // existed carries no Provenance, and — verified against embedded RavenDB in
            // ProvenanceBacklogQueryTests — an absent property does NOT satisfy `Provenance = null`;
            // only `not exists(Provenance)` reaches it. Matching just the enum name, or trusting the null
            // equality, would silently skip precisely the corpus this query exists to drain.
            .OpenSubclause()
            .WhereEquals("Provenance", nameof(MemoryProvenance.Unknown))
            .OrElse()
            .Not
            .WhereExists("Provenance")
            .CloseSubclause()
            .OrderBy("CreatedAt")
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<Dictionary<MemoryType, int>> GetCountsByTypeAsync(
        string repoId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var counts = await session
            .Query<Memories_CountByType.Result, Memories_CountByType>()
            .Where(r => r.RepoId == repoId)
            .ToListAsync(ct);

        return counts.ToDictionary(c => c.Type, c => c.Count);
    }

    public async Task<List<MemoryEntry>> GetTopScoredAsync(
        string repoId, MemoryType[] types, int limit, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        // Over-fetch relative to the caller's limit. Importance is the only orderable proxy in the
        // index, but callers re-rank on signals it cannot see (access frequency, dual-clock recency),
        // so a pool cut at exactly `limit` by importance decides the outcome before the real ranking
        // runs — a high-importance seed with no usage permanently outranks earned knowledge that
        // would win on the full score. The client-side IsLatest filter also shrinks the pool, and
        // that shrinkage must not eat into the caller's budget.
        var poolSize = Math.Min(limit * PoolOverfetch, MaxPool);
        var results = await session.Advanced
            .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
            .WhereEquals("RepoId", repoId)
            .WhereEquals("ValidUntil", (DateTime?)null)
            .WhereIn("Type", types.Cast<object>())
            .OrderByDescending("Importance")
            .Take(poolSize)
            .ToListAsync(ct);

        // Filter to IsLatest client-side (not in the search index)
        return results.Where(e => e.IsLatest).ToList();
    }

    // Both unenriched queries run against a collection auto-index rather than Memories_Search:
    // Summary/IsLatest aren't in the static index, and extending it would force a full reindex
    // of the corpus for a maintenance-only query path.

    public async Task<List<MemoryEntry>> GetUnenrichedAsync(string repoId, int limit, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        return await session.Query<MemoryEntry>()
            .Where(e => e.RepoId == repoId && e.Summary == null && e.Validity.ValidUntil == null && e.IsLatest)
            .OrderBy(e => e.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<UnenrichedStats> GetUnenrichedStatsAsync(string? repoId = null, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();

        var count = await Query().CountAsync(ct);
        if (count == 0) return new UnenrichedStats(0, null);

        var oldest = await Query().OrderBy(e => e.CreatedAt).FirstOrDefaultAsync(ct);
        return new UnenrichedStats(count, oldest?.CreatedAt);

        IQueryable<MemoryEntry> Query()
        {
            var q = session.Query<MemoryEntry>()
                .Where(e => e.Summary == null && e.Validity.ValidUntil == null && e.IsLatest);
            return repoId is null ? q : q.Where(e => e.RepoId == repoId);
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var operation = new GetDatabaseRecordOperation(_store.Database);
            var result = await _store.Maintenance.Server.SendAsync(operation, ct);
            return result != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DatabaseInfo?> GetDatabaseInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var dbRecord = await _store.Maintenance.Server.SendAsync(
                new GetDatabaseRecordOperation(_store.Database), ct);
            if (dbRecord == null) return null;

            var stats = await _store.Maintenance.SendAsync(
                new Raven.Client.Documents.Operations.GetStatisticsOperation(), ct);

            var serverVersion = "unknown";
            try
            {
                var buildNumber = await _store.Maintenance.Server.SendAsync(
                    new GetBuildNumberOperation(), ct);
                serverVersion = buildNumber.FullVersion;
            }
            catch { }

            var indexExists = stats.Indexes.Any(i => i.Name == Memories_Search.IndexName_);

            return new DatabaseInfo(
                Name: _store.Database,
                ServerVersion: serverVersion,
                DocumentCount: stats.CountOfDocuments,
                IndexExists: indexExists);
        }
        catch
        {
            return null;
        }
    }

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await IndexCreation.CreateIndexesAsync(
            typeof(Memories_Search).Assembly, _store, token: ct);
    }

    public async Task<List<string>> GetDistinctRepoIdsAsync(CancellationToken ct = default)
    {
        try
        {
            using var session = _store.OpenAsyncSession();
            var repoIds = await session.Advanced
                .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
                .SelectFields<RepoIdProjection>("RepoId")
                .Take(1000)
                .ToListAsync(ct);

            return repoIds
                .Select(r => r.RepoId)
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async Task<List<MemoryEntry>> BrowseAsync(string repoId, int skip, int take, MemoryType? type = null, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var query = session.Advanced
            .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
            .WhereEquals("RepoId", repoId)
            .WhereEquals("ValidUntil", (DateTime?)null);

        if (type.HasValue)
            query = query.WhereEquals("Type", type.Value);

        return await query
            .OrderByDescending("CreatedAt")
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    private class RepoIdProjection { public string RepoId { get; set; } = ""; }

    // ─── Layer operations ─────────────────────────────────────────────

    public async Task<string> StoreMountedLayerAsync(MemoryLayer layer, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(layer, layer.Id, ct);
        await session.SaveChangesAsync(ct);
        return layer.Id;
    }

    public async Task<bool> UnmountLayerAsync(string layerId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var layer = await session.LoadAsync<MemoryLayer>(layerId, ct);
        if (layer is null) return false;
        session.Delete(layer);
        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<MemoryLayer>> GetMountedLayersAsync(string repoId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var layers = await session.Query<MemoryLayer>()
            .ToListAsync(ct);

        // Filter to applicable layers: universal, or repo is in ApplicableRepos
        return layers.Where(l =>
            l.ApplicableRepos.Count == 0 ||
            l.ApplicableRepos.Contains(repoId, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public async Task<MemoryLayer?> GetLayerAsync(string layerId, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        return await session.LoadAsync<MemoryLayer>(layerId, ct);
    }

    public async Task<List<MemoryEntry>> GetByLayerIdAsync(string layerId, CancellationToken ct = default)
    {
        var allEntries = new List<MemoryEntry>();
        using var session = _store.OpenAsyncSession();
        var skip = 0;
        const int pageSize = 256;
        while (true)
        {
            var batch = await session.Advanced
                .AsyncDocumentQuery<MemoryEntry, Memories_Search>()
                .WhereEquals("LayerId", layerId)
                .Skip(skip)
                .Take(pageSize)
                .ToListAsync(ct);

            allEntries.AddRange(batch);
            if (batch.Count < pageSize) break;
            skip += pageSize;
        }
        return allEntries;
    }

    public async Task<bool> HardDeleteAsync(string id, CancellationToken ct = default)
    {
        using var session = _store.OpenAsyncSession();
        var entry = await session.LoadAsync<MemoryEntry>(id, ct);
        if (entry is null) return false;
        session.Delete(entry);
        await session.SaveChangesAsync(ct);
        return true;
    }

    private static IAsyncDocumentQuery<MemoryEntry> ApplyFilters(
        IAsyncDocumentQuery<MemoryEntry> documentQuery, MemoryQuery query)
    {
        if (query.Type.HasValue)
            documentQuery = documentQuery.AndAlso().WhereEquals("Type", query.Type.Value);

        if (query.Valence.HasValue)
            documentQuery = documentQuery.AndAlso().WhereEquals("Valence", query.Valence.Value);

        // None-as-wildcard hard pre-filter: admit stage-`z` AND stage-agnostic (None) memories, exclude
        // memories that declare a DIFFERENT stage. The grouped subclause is mandatory — an ungrouped
        // OrElse would bind across the whole query and leak every None memory past the repo/type/valence
        // isolation clauses. Precision rises as the corpus is tagged; degrades to today's behavior (never
        // a blackout) when it isn't.
        if (query.Stage.HasValue)
            documentQuery = documentQuery.AndAlso().OpenSubclause()
                .WhereEquals("Stage", query.Stage.Value)
                .OrElse().WhereEquals("Stage", FunctionalStage.None)
                .CloseSubclause();

        foreach (var tag in query.Tags)
            documentQuery = documentQuery.AndAlso().WhereIn("Tags", new[] { tag });

        if (!query.IncludeExpired)
            documentQuery = documentQuery.AndAlso().WhereEquals("ValidUntil", (DateTime?)null);

        return documentQuery;
    }
}
