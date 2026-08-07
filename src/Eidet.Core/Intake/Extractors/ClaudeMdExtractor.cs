using Eidet.Core.Domain;

namespace Eidet.Core.Intake.Extractors;

/// <summary>
/// Pulls CLAUDE.md and the legacy MEMORY.md from the project root, splits each on
/// H1–H3 headings, and emits one Insight memory per section. Higher importance (0.8)
/// than the README extractor since these files are explicitly hand-curated for agents.
/// </summary>
public sealed class ClaudeMdExtractor : IIntakeExtractor
{
    private static readonly string[] FileNames = ["CLAUDE.md", "MEMORY.md"];

    public string Name => "markdown.claude";

    public bool AppliesTo(IntakeContext ctx) =>
        FileNames.Any(f => File.Exists(Path.Combine(ctx.ProjectPath, f)));

    public async Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct)
    {
        foreach (var name in FileNames)
        {
            var path = Path.Combine(ctx.ProjectPath, name);
            if (!File.Exists(path)) continue;

            var content = await File.ReadAllTextAsync(path, ct);
            foreach (var (sectionContent, tags) in MarkdownIntake.SplitByHeadings(content))
            {
                await sink.AddMemoryAsync(
                    new IntakeMemory(name, MemoryType.Insight, sectionContent.Trim(), tags, Importance: 0.5f),
                    ct);
            }
        }
    }
}
