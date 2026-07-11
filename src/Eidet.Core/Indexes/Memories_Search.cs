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
            select new Result
            {
                Content = e.Content,
                SearchText = searchText,
                SearchVector = CreateVector(searchText),
                RepoId = e.RepoId,
                Type = e.Type,
                Valence = e.Valence,
                Stage = e.Stage,
                Tags = e.Tags.ToArray(),
                Entities = e.Entities.ToArray(),
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

        StoreAllFields(FieldStorage.Yes);
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
    }
}
