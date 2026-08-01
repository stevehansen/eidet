using System.Security.Cryptography;
using System.Text;
using Eidet.Core.Domain;
using Eidet.Core.Gates;
using Eidet.Core.Intake;
using Eidet.Core.Intake.Extractors;
using Eidet.Core.Intake.Git;
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

    /// <summary>
    /// The built-in extractor list — markdown (CLAUDE/MEMORY/AGENTS/README), editorconfig,
    /// NuGet, npm, plus the option-gated docs-folder and Claude Code memory extractors
    /// (inactive until their <see cref="IntakeOptions"/> switch is set).
    /// </summary>
    public static IIntakeExtractor[] DefaultExtractors() =>
    [
        new ClaudeMdExtractor(),
        new AgentsMdExtractor(),
        new ReadmeExtractor(),
        new EditorConfigExtractor(),
        new DocsFolderExtractor(),
        new ClaudeCodeMemoryExtractor(),
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

    /// <summary>
    /// Git-history intake: mines merged commit history into seed Procedure/Insight memories.
    /// Runs only the registered <see cref="GitHistoryExtractor"/>s (default: a git-CLI-backed
    /// one created for <paramref name="projectPath"/>) so file extractors never ride along.
    /// Unless <paramref name="options"/>.Since is set, resumes from the per-repo watermark,
    /// which a non-dry run advances to the repo tip afterwards.
    /// </summary>
    public async Task<IntakeResult> IngestGitAsync(
        string repoId, string projectPath,
        GitIntakeOptions? options = null, bool dryRun = false, CancellationToken ct = default)
    {
        options ??= new GitIntakeOptions();
        var normalizedRepo = RepoIdNormalizer.Normalize(repoId);

        var extractors = _extractors.OfType<GitHistoryExtractor>().ToList();
        if (extractors.Count == 0)
            extractors = [new GitHistoryExtractor(
                (IGitHistorySource?)GitCliAdapter.TryCreate(projectPath) ?? NullGitHistorySource.Instance)];

        var since = options.Since ?? await _store.GetGitIntakeWatermarkAsync(normalizedRepo, ct);
        var ctx = new IntakeContext
        {
            RepoId = normalizedRepo,
            ProjectPath = projectPath,
            DryRun = dryRun,
            Options = new IntakeOptions { Git = options with { Since = since } },
        };

        if (!extractors.Any(e => e.AppliesTo(ctx)))
        {
            var unavailable = new IntakeResult { SkippedCount = 1 };
            unavailable.Items.Add(new IntakeItem
            {
                Source = "git",
                WasSkipped = true,
                SkipReason = "not a git repository (or git unavailable)",
            });
            return unavailable;
        }

        // Tip is read BEFORE the pipeline so a commit landing mid-run stays ahead of the
        // watermark and is picked up next run (at-least-once; content-hash dedup absorbs replays).
        var tip = dryRun ? null : await ReadTipShaAsync(extractors[0].Source, ct);

        var result = await RunPipelineAsync(ctx, extractors, ct);

        if (tip is not null)
            await _store.SetGitIntakeWatermarkAsync(normalizedRepo, tip, ct);
        return result;
    }

    private static async Task<string?> ReadTipShaAsync(IGitHistorySource git, CancellationToken ct)
    {
        await foreach (var commit in git.ReadMergedHistoryAsync(new GitHistoryQuery(MaxCommits: 1), ct))
            return commit.Sha;
        return null;
    }

    /// <summary>
    /// Claude Code native memory import: ingests <c>~/.claude/projects/&lt;slug&gt;/memory/*.md</c>
    /// for <paramref name="projectPath"/> as seed memories. Opt-in sibling verb because the
    /// source lies outside the repo; runs only the registered
    /// <see cref="ClaudeCodeMemoryExtractor"/>s so file extractors never ride along.
    /// </summary>
    public async Task<IntakeResult> IngestClaudeMemoryAsync(
        string repoId, string projectPath, bool dryRun = false, CancellationToken ct = default)
    {
        var extractors = _extractors.OfType<ClaudeCodeMemoryExtractor>().ToList();
        if (extractors.Count == 0)
            extractors = [new ClaudeCodeMemoryExtractor()];

        var ctx = new IntakeContext
        {
            RepoId = RepoIdNormalizer.Normalize(repoId),
            ProjectPath = projectPath,
            DryRun = dryRun,
            Options = new IntakeOptions { ClaudeMemory = true },
        };

        if (!extractors.Any(e => e.AppliesTo(ctx)))
        {
            var unavailable = new IntakeResult { SkippedCount = 1 };
            unavailable.Items.Add(new IntakeItem
            {
                Source = "claude-memory",
                WasSkipped = true,
                SkipReason = "no Claude Code memory directory for this project",
            });
            return unavailable;
        }

        return await RunPipelineAsync(ctx, extractors, ct);
    }

    private Task<IntakeResult> RunPipelineAsync(IntakeContext ctx, CancellationToken ct) =>
        RunPipelineAsync(ctx, _extractors, ct);

    private Task<IntakeResult> RunPipelineAsync(
        IntakeContext ctx, IReadOnlyList<IIntakeExtractor> extractors, CancellationToken ct) =>
        // Validate=false is deliberate: BulkMutationCtx's validate path throws and aborts the
        // whole batch on the first bad candidate. Intake instead runs WriteValidator per
        // candidate inside OrchestratorSink (skip-not-abort), so the write gate still covers
        // every stored candidate without one secret-bearing candidate sinking the run.
        _memory.RunBulkAsync(async bulk =>
        {
            var sink = new OrchestratorSink(_store, bulk, ctx);
            foreach (var extractor in extractors)
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

            // Always-on write gate, per candidate: a secret-bearing or low-signal candidate is
            // skipped with the gate's reason surfaced and the batch continues — intake never
            // bulk-aborts on one bad candidate and never stores unscanned content (closes the
            // BulkOptions.Validate=false bypass; issue #63, STRIDE T-15/I-7). The content is
            // redacted from the result item so a caught secret can't leak via CLI/REST output.
            var validation = WriteValidator.Validate(candidate.Content, candidate.Type);
            if (!validation.Passed)
            {
                item.Content = "";
                Skip(item, $"{validation.FailedGate}: {validation.Reason}");
                return;
            }

            // Content-addressed so re-ingesting an unchanged file collides with the existing document and
            // skips below. Minted through MemoryIdGenerator (not locally) so the content-commitment check
            // recognizes the convention instead of reading every intake memory as rewritten content.
            var id = MemoryIdGenerator.GenerateContentAddressed(_ctx.RepoId, candidate.Type, candidate.Content);
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
