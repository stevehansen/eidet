using Eidet.Core.Indexes;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Indexes.Vector;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Raven.Client.Documents.Operations.AI;
using Raven.Client.Documents.Operations.Refresh;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Database;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace Eidet.Core.Storage;

public static class DatabaseProvisioner
{
    public const string ConnectionStringName = "LocalEmbeddings";
    public const string EmbeddingsTaskId = "memory-embeddings";

    public static bool DatabaseExists(IDocumentStore store)
    {
        try
        {
            store.Maintenance.ForDatabase(store.Database).Send(new GetStatisticsOperation());
            return true;
        }
        catch (DatabaseDoesNotExistException)
        {
            return false;
        }
    }

    public static void EnsureDatabaseExists(IDocumentStore store)
    {
        if (!DatabaseExists(store))
            store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(store.Database)));
    }

    public static void DeployIndexes(IDocumentStore store)
    {
        IndexCreation.CreateIndexes(typeof(Memories_Search).Assembly, store);
    }

    /// <summary>
    /// Enables the RavenDB Refresh feature on the database. Required for
    /// persisted scheduled tasks that use @refresh metadata to trigger at a future time.
    /// Idempotent — safe to call on every startup.
    /// </summary>
    public static void EnsureRefreshEnabled(IDocumentStore store)
    {
        try
        {
            store.Maintenance.Send(new ConfigureRefreshOperation(
                new RefreshConfiguration { Disabled = false }));
        }
        catch
        {
            // May fail on older RavenDB versions — scheduler will still work via polling fallback
        }
    }

    /// <summary>
    /// Enables a bounded revisions trail on the <c>MemoryFiles</c> collection. Memory-tool blobs
    /// are overwritten in place (the byte-exact contract), so revisions are their only edit
    /// history — unlike memories, which get supersession chains. Bounded to 10 per file so the
    /// audit trail can't amplify write volume unboundedly. Idempotent — safe on every startup.
    /// Note: this replaces the database's revisions configuration; nothing else in Eidet
    /// configures revisions today.
    /// </summary>
    public static void EnsureMemoryFileRevisions(IDocumentStore store)
    {
        try
        {
            store.Maintenance.Send(new ConfigureRevisionsOperation(new RevisionsConfiguration
            {
                Collections = new Dictionary<string, RevisionsCollectionConfiguration>
                {
                    ["MemoryFiles"] = new() { Disabled = false, MinimumRevisionsToKeep = 10 },
                },
            }));
        }
        catch
        {
            // Non-fatal: the memory tool still works without revisions, just without the audit trail.
        }
    }

    public static string? EnsureEmbeddingsConfigured(IDocumentStore store)
    {
        // 1. Create AI connection string for the embedded bge-micro-v2 model
        try
        {
            var connectionString = new AiConnectionString
            {
                Name = ConnectionStringName,
                EmbeddedSettings = new EmbeddedSettings()
            };

            store.Maintenance.Send(
                new PutConnectionStringOperation<AiConnectionString>(connectionString));
        }
        catch
        {
            // Connection string may already exist
        }

        // 2. Create embeddings generation task
        try
        {
            var taskConfig = new EmbeddingsGenerationConfiguration
            {
                Name = "MemoryEntries Content Embeddings",
                Identifier = EmbeddingsTaskId,
                ConnectionStringName = ConnectionStringName,
                Collection = "MemoryEntries",
                EmbeddingsPathConfigurations =
                [
                    new EmbeddingPathConfiguration
                    {
                        Path = "Content",
                        ChunkingOptions = new ChunkingOptions
                        {
                            ChunkingMethod = ChunkingMethod.PlainTextSplitParagraphs,
                            MaxTokensPerChunk = 512,
                        }
                    }
                ],
                Quantization = VectorEmbeddingType.Single,
                ChunkingOptionsForQuerying = new ChunkingOptions
                {
                    ChunkingMethod = ChunkingMethod.PlainTextSplitParagraphs,
                    MaxTokensPerChunk = 512,
                },
            };

            store.Maintenance.Send(new AddEmbeddingsGenerationOperation(taskConfig));
            return null;
        }
        catch (Exception ex) when (ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase)
                                   || ex.InnerException?.Message.Contains("already", StringComparison.OrdinalIgnoreCase) == true)
        {
            return null; // Already exists
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
