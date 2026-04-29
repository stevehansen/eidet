using Eidet.Core.Domain;

namespace Eidet.Core.Intake.Extractors;

/// <summary>
/// Splits README.md into per-heading Insight memories at importance 0.6 — README content
/// is broadly useful but less curated for agents than CLAUDE.md, so it sits below the
/// CLAUDE/MEMORY tier.
/// </summary>
public sealed class ReadmeExtractor : IIntakeExtractor
{
    private const string FileName = "README.md";

    public string Name => "markdown.readme";

    public bool AppliesTo(IntakeContext ctx) =>
        File.Exists(Path.Combine(ctx.ProjectPath, FileName));

    public async Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct)
    {
        var path = Path.Combine(ctx.ProjectPath, FileName);
        var content = await File.ReadAllTextAsync(path, ct);
        foreach (var (sectionContent, tags) in MarkdownIntake.SplitByHeadings(content))
        {
            await sink.AddMemoryAsync(
                new IntakeMemory(FileName, MemoryType.Insight, sectionContent.Trim(), tags, Importance: 0.6f),
                ct);
        }
    }
}
