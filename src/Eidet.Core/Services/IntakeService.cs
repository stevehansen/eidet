using System.Security.Cryptography;
using System.Text;
using Eidet.Core.Domain;
using Eidet.Core.Intake;
using Eidet.Core.Intake.Extractors;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

/// <summary>
/// Orchestrates the intake pipeline. Owns dedup-by-content-hash, store plumbing,
/// dry-run semantics, and result aggregation; delegates all per-ecosystem parsing
/// to <see cref="IIntakeExtractor"/>s.
/// </summary>
/// <remarks>
/// The default extractor list covers CLAUDE/MEMORY/README markdown, .editorconfig,
/// NuGet, and npm. <see cref="DocsFolderExtractor"/> is included but inactive by
/// default — it only runs when <see cref="IntakeOptions.DocsPattern"/> is set, which
/// is how <see cref="IngestDocsAsync"/> activates it.
/// </remarks>
public class IntakeService
{
    private readonly IEidetStore _store;
    private readonly MemoryService _memory;
    private readonly IReadOnlyList<IIntakeExtractor> _extractors;

    /// <summary>
    /// Constructs a service with the default extractor list. Tests and SDK consumers
    /// that want a custom registry should use the <see cref="IntakeService(IEidetStore, IEnumerable{IIntakeExtractor}, MemoryService)"/>
    /// overload.
    /// </summary>
    public IntakeService(IEidetStore store, MemoryService memory)
        : this(store, DefaultExtractors(), memory)
    {
    }

    public IntakeService(IEidetStore store, IEnumerable<IIntakeExtractor> extractors, MemoryService memory)
    {
        _store = store;
        _memory = memory;
        _extractors = extractors.ToList();
    }

    /// <summary>The built-in extractor list — markdown, editorconfig, NuGet, npm, plus the inactive docs-folder.</summary>
    public static IIntakeExtractor[] DefaultExtractors() =>
    [
        new ClaudeMdExtractor(),
        new ReadmeExtractor(),
        new EditorConfigExtractor(),
        new DocsFolderExtractor(),
        new NuGetDependencyExtractor(),
        new NpmDependencyExtractor(),
    ];

    /// <summary>Whole-repo intake: runs every extractor whose <see cref="IIntakeExtractor.AppliesTo"/> returns true.</summary>
    public Task<IntakeResult> IngestAsync(string repoId, string projectPath, bool dryRun = false, CancellationToken ct = default)
    {
        var ctx = new IntakeContext
        {
            RepoId = RepoIdNormalizer.Normalize(repoId),
            ProjectPath = projectPath,
            DryRun = dryRun,
        };
        return RunPipelineAsync(ctx, ct);
    }

    /// <summary>
    /// Path-scoped intake: walks <paramref name="docsPath"/> with the given pattern and
    /// activates only the docs-folder extractor.
    /// </summary>
    public Task<IntakeResult> IngestDocsAsync(
        string repoId, string docsPath, bool recursive = true, string pattern = "*.md",
        float importance = 0.6f, List<string>? extraTags = null, bool dryRun = false, CancellationToken ct = default)
    {
        var ctx = new IntakeContext
        {
            RepoId = RepoIdNormalizer.Normalize(repoId),
            ProjectPath = docsPath,
            DryRun = dryRun,
            Options = new IntakeOptions
            {
                DocsPattern = pattern,
                DocsRecursive = recursive,
                DocsImportance = importance,
                DocsExtraTags = extraTags,
            },
        };
        return RunPipelineAsync(ctx, ct);
    }

    private Task<IntakeResult> RunPipelineAsync(IntakeContext ctx, CancellationToken ct) =>
        _memory.RunBulkAsync(async bulk =>
        {
            var sink = new OrchestratorSink(_store, bulk, ctx);
            foreach (var extractor in _extractors)
            {
                if (!extractor.AppliesTo(ctx)) continue;
                await extractor.ExtractAsync(ctx, sink, ct);
            }
            return sink.Build();
        }, new BulkOptions { OperationName = "intake" }, ct);

    private sealed class OrchestratorSink : IIntakeSink
    {
        private readonly IEidetStore _store;
        private readonly BulkMutationCtx _bulk;
        private readonly IntakeContext _ctx;
        private readonly IntakeResult _result = new();

        public OrchestratorSink(IEidetStore store, BulkMutationCtx bulk, IntakeContext ctx)
        {
            _store = store;
            _bulk = bulk;
            _ctx = ctx;
        }

        public IntakeResult Build() => _result;

        public async ValueTask AddMemoryAsync(IntakeMemory candidate, CancellationToken ct)
        {
            var item = new IntakeItem
            {
                Source = candidate.Source,
                Type = candidate.Type,
                Content = candidate.Content,
                Tags = candidate.Tags.ToList(),
            };

            if (candidate.Content.Length < MarkdownIntake.MinSectionLength)
            {
                Skip(item, "too short");
                return;
            }

            var hash = ComputeContentHash(candidate.Content);
            var id = $"memories/{_ctx.RepoId}/{candidate.Type.ToString().ToLowerInvariant()}/{hash}";
            var existing = await _store.GetAsync(id, ct);
            if (existing != null)
            {
                Skip(item, "duplicate");
                return;
            }

            if (!_ctx.DryRun)
            {
                var now = DateTime.UtcNow;
                var entry = new MemoryEntry
                {
                    Id = id,
                    RepoId = _ctx.RepoId,
                    Type = candidate.Type,
                    Content = candidate.Content,
                    Tags = item.Tags,
                    Importance = candidate.Importance,
                    Source = "intake",
                    Provenance = MemoryProvenance.Intake,
                    Confidence = 0.7f,
                    CreatedAt = now,
                    Validity = new Validity { ValidFrom = now },
                    Entities = EntityExtractor.Extract(candidate.Content),
                    OneLiner = EntityExtractor.GenerateHeuristicOneLiner(candidate.Content),
                };
                await _bulk.StoreNewAsync(entry, ct);
            }

            _result.Items.Add(item);
            _result.NewCount++;
        }

        public void AddLink(MemoryLink link) => _result.DetectedLinks.Add(link);

        public void AddProducedPackage(string packageId) => _result.ProducedPackages.Add(packageId);

        public void RecordSkipped(string source, string reason)
        {
            var item = new IntakeItem { Source = source, Type = MemoryType.Observation, Content = "" };
            Skip(item, reason);
        }

        private void Skip(IntakeItem item, string reason)
        {
            item.WasSkipped = true;
            item.SkipReason = reason;
            _result.Items.Add(item);
            _result.SkippedCount++;
        }

        private static string ComputeContentHash(string content)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
            return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
        }
    }
}

public class IntakeResult
{
    public List<IntakeItem> Items { get; set; } = [];
    public int NewCount { get; set; }
    public int SkippedCount { get; set; }
    public List<MemoryLink> DetectedLinks { get; set; } = [];
    public List<string> ProducedPackages { get; set; } = [];
}

public class IntakeItem
{
    public string Source { get; set; } = "";
    public MemoryType Type { get; set; }
    public string Content { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public bool WasSkipped { get; set; }
    public string? SkipReason { get; set; }
}
