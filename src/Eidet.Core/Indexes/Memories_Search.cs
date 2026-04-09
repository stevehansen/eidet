using Eidet.Core.Domain;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Indexes.Vector;

namespace Eidet.Core.Indexes;

public class Memories_Search : AbstractIndexCreationTask<MemoryEntry, Memories_Search.Result>
{
    public class Result
    {
        public string Content { get; set; } = "";
        public object? ContentVector { get; set; }
        public string RepoId { get; set; } = "";
        public MemoryType Type { get; set; }
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
            select new Result
            {
                Content = e.Content,
                ContentVector = CreateVector(e.Content),
                RepoId = e.RepoId,
                Type = e.Type,
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

        // Full-text search on content + entities
        Index("Content", FieldIndexing.Search);
        Analyze("Content", "StandardAnalyzer");

        Index("Entities", FieldIndexing.Search);
        Analyze("Entities", "KeywordAnalyzer");

        // Vector search (RavenDB built-in embeddings)
        VectorIndexes.Add(x => x.ContentVector, new VectorOptions
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
