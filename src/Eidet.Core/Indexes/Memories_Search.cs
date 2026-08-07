using Eidet.Core.Domain;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Indexes.Vector;

namespace Eidet.Core.Indexes;

public class Memories_Search : AbstractIndexCreationTask<MemoryEntry, Memories_Search.Result>
{
    public const string IndexName_ = "Memories/Search";

    public class Result
    {
        public string Content { get; set; } = "";
        /// <summary>
        /// Composite field: Content + Summary + OneLiner + Tags joined.
        /// Used for full-text search so queries match across all textual fields.
        /// </summary>
        public string SearchText { get; set; } = "";
        public object? SearchVector { get; set; }

        /// <summary>
        /// The memory's ABSTRACTION — its shortest faithful self-description — as opposed to
        /// <see cref="SearchText"/>, which is the whole entry. Derived, never stored: the first non-empty
        /// of OneLiner, Summary, Content, clamped. Its own vector arm exists because a long Content
        /// dominates a composite embedding and drowns the crisp one-liner it is wrapped with.
        /// </summary>
        public string AbstractionText { get; set; } = "";
        public object? AbstractionVector { get; set; }
        public string RepoId { get; set; } = "";
        public MemoryType Type { get; set; }
        public Valence Valence { get; set; }
        public FunctionalStage Stage { get; set; }
        public string[] Tags { get; set; } = [];
        public string[] Entities { get; set; } = [];
        public DateTime CreatedAt { get; set; }
        public DateTime? ValidUntil { get; set; }
        public float Importance { get; set; }
        public int AccessCount { get; set; }
        public string? Summary { get; set; }
        public string? OneLiner { get; set; }
        public string? LayerId { get; set; }
        public MemoryProvenance Provenance { get; set; }
        public string? ForesightHint { get; set; }
    }

    public Memories_Search()
    {
        Map = entries => from e in entries
            let searchText = string.Join(" ",
                new[] { e.Content, e.Summary, e.OneLiner, e.ForesightHint }
                    .Where(s => s != null))
                + " " + string.Join(" ", e.Tags)
                + " " + string.Join(" ", e.Entities)
            // Deliberately IsNullOrEmpty, not ??: null means "awaiting enrichment" but EMPTY means
            // redacted, and a redacted one-liner must fall through rather than embed nothing. The
            // Content fallback is what keeps this arm dense on a zero-LLM write path — every entry
            // has an abstraction from the moment it is stored, enriched or not. Clamped because an
            // abstraction that runs on is no longer an abstraction.
            let abstraction = !string.IsNullOrEmpty(e.OneLiner) ? e.OneLiner
                : !string.IsNullOrEmpty(e.Summary) ? e.Summary
                : e.Content
            let abstractionText = abstraction.Length > 200 ? abstraction.Substring(0, 200) : abstraction
            select new Result
            {
                Content = e.Content,
                SearchText = searchText,
                SearchVector = CreateVector(searchText),
                AbstractionText = abstractionText,
                AbstractionVector = CreateVector(abstractionText),
                RepoId = e.RepoId,
                Type = e.Type,
                Valence = e.Valence,
                Stage = e.Stage,
                Tags = e.Tags.ToArray(),
                // Lower-cased so the cue-anchor term lookup is case-insensitive: KeywordAnalyzer
                // preserves case, so an "Ollama" cue would otherwise miss an "ollama" entity and
                // enrichment casing would silently decide reachability. Invisible to callers —
                // queries over this index return the DOCUMENT, never this projection.
                Entities = e.Entities.Select(x => x.ToLowerInvariant()).ToArray(),
                CreatedAt = e.CreatedAt,
                ValidUntil = e.Validity.ValidUntil,
                Importance = e.Importance,
                AccessCount = e.AccessCount,
                Summary = e.Summary,
                OneLiner = e.OneLiner,
                LayerId = e.LayerId,
                Provenance = e.Provenance,
                ForesightHint = e.ForesightHint,
            };

        // Full-text search on composite SearchText field (Content + Summary + OneLiner + Tags + Entities)
        Index("SearchText", FieldIndexing.Search);
        Analyze("SearchText", "StandardAnalyzer");

        // Keep Content indexed for backward compat
        Index("Content", FieldIndexing.Search);
        Analyze("Content", "StandardAnalyzer");

        Index("Entities", FieldIndexing.Search);
        Analyze("Entities", "KeywordAnalyzer");

        // Vector search on composite text (includes Summary + OneLiner for richer embeddings)
        VectorIndexes.Add(x => x.SearchVector, new VectorOptions
        {
            SourceEmbeddingType = VectorEmbeddingType.Text,
            DestinationEmbeddingType = VectorEmbeddingType.Single,
            NumberOfEdges = 20,
            NumberOfCandidatesForIndexing = 50,
        });

        // Second, narrower vector: the abstraction alone. Same embedder, so the two arms are
        // comparable; different text, so a query that matches what a memory IS scores here even
        // when the composite vector is diluted by a long body.
        VectorIndexes.Add(x => x.AbstractionVector, new VectorOptions
        {
            SourceEmbeddingType = VectorEmbeddingType.Text,
            DestinationEmbeddingType = VectorEmbeddingType.Single,
            NumberOfEdges = 20,
            NumberOfCandidatesForIndexing = 50,
        });

        StoreAllFields(FieldStorage.Yes);
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
    }
}
