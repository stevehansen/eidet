using Eidet.Core.Domain;

namespace Eidet.Core.Intake.Extractors;

/// <summary>
/// Ingests <c>AGENTS.md</c> files — the cross-tool agent-instruction convention used by
/// Codex/Cursor/Gemini — from the repo root and nested directories, splitting each on
/// H1–H3 headings into Insight memories. Same importance as <see cref="ClaudeMdExtractor"/>
/// (0.8): both are hand-curated for agents. Kept separate from the CLAUDE.md extractor so
/// provenance and diagnostics stay legible per ecosystem.
/// </summary>
public sealed class AgentsMdExtractor : IIntakeExtractor
{
    private const string FileName = "AGENTS.md";

    /// <summary>Directory names never worth walking for nested AGENTS.md files.</summary>
    private static readonly string[] NoiseDirectories = [".git", "node_modules", "bin", "obj", ".claude"];

    public string Name => "markdown.agents";

    public bool AppliesTo(IntakeContext ctx) =>
        Directory.Exists(ctx.ProjectPath) && FindFiles(ctx.ProjectPath).Any();

    public async Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct)
    {
        foreach (var file in FindFiles(ctx.ProjectPath))
        {
            var relativePath = Path.GetRelativePath(ctx.ProjectPath, file);
            var content = await File.ReadAllTextAsync(file, ct);
            var fileTags = MarkdownIntake.TagsFromFileName(relativePath);

            foreach (var (sectionContent, headingTags) in MarkdownIntake.SplitByHeadings(content))
            {
                var tags = new List<string>(headingTags);
                tags.AddRange(fileTags);
                await sink.AddMemoryAsync(
                    new IntakeMemory(relativePath, MemoryType.Insight, sectionContent.Trim(),
                        tags.Distinct().ToList(), Importance: 0.5f),
                    ct);
            }
        }
    }

    private static IEnumerable<string> FindFiles(string projectPath) =>
        Directory.EnumerateFiles(projectPath, FileName, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        }).Where(f => !IsInNoiseDirectory(Path.GetRelativePath(projectPath, f)));

    private static bool IsInNoiseDirectory(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => NoiseDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
}
