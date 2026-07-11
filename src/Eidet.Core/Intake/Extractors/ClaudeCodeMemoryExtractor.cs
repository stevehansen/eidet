using Eidet.Core.Domain;

namespace Eidet.Core.Intake.Extractors;

/// <summary>
/// Ingests Claude Code's native per-project memory directory —
/// <c>~/.claude/projects/&lt;slug&gt;/memory/*.md</c> — as Insight memories (heading-split,
/// importance 0.7: auto-accumulated agent notes, less curated than CLAUDE.md). Because it
/// reads OUTSIDE the repo, it is opt-in via <see cref="IntakeOptions.ClaudeMemory"/> (the
/// <c>eidet intake-claude-memory</c> sibling verb) and never runs in the default intake
/// pass. Reads are constrained to the single resolved per-project memory directory —
/// never arbitrary home paths (STRIDE I-8). Strictly additive to Claude Code's own
/// auto-memory; Eidet never writes there.
/// </summary>
public sealed class ClaudeCodeMemoryExtractor : IIntakeExtractor
{
    private readonly string _claudeHome;

    /// <param name="claudeHome">Base <c>.claude</c> directory; defaults to the user profile.
    /// Injectable so tests never touch the real home directory.</param>
    public ClaudeCodeMemoryExtractor(string? claudeHome = null) =>
        _claudeHome = claudeHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public string Name => "claude-code.memory";

    public bool AppliesTo(IntakeContext ctx) =>
        ctx.Options.ClaudeMemory && Directory.Exists(ResolveMemoryDir(ctx.ProjectPath));

    public async Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct)
    {
        var memoryDir = ResolveMemoryDir(ctx.ProjectPath);
        foreach (var file in Directory.GetFiles(memoryDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            var content = await File.ReadAllTextAsync(file, ct);
            var fileTags = MarkdownIntake.TagsFromFileName(fileName);

            foreach (var (sectionContent, headingTags) in MarkdownIntake.SplitByHeadings(content))
            {
                var tags = new List<string> { "claude-code" };
                tags.AddRange(headingTags);
                tags.AddRange(fileTags);
                await sink.AddMemoryAsync(
                    new IntakeMemory($"claude-memory/{fileName}", MemoryType.Insight,
                        sectionContent.Trim(), tags.Distinct().ToList(), Importance: 0.7f),
                    ct);
            }
        }
    }

    /// <summary>
    /// The single directory this extractor may read. Claude Code slugs a project path the
    /// same way <see cref="RepoIdNormalizer.Normalize"/> does (path separators, <c>:</c>,
    /// <c>.</c> and <c>_</c> become <c>-</c>), so the normalizer doubles as the slug rule;
    /// a mismatch merely makes <see cref="AppliesTo"/> false.
    /// </summary>
    private string ResolveMemoryDir(string projectPath) =>
        Path.Combine(_claudeHome, "projects", RepoIdNormalizer.Normalize(projectPath), "memory");
}
