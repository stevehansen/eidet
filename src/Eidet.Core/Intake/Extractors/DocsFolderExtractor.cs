using Eidet.Core.Domain;

namespace Eidet.Core.Intake.Extractors;

/// <summary>
/// Walks <see cref="IntakeContext.ProjectPath"/> for a glob pattern and ingests each
/// matching markdown file as one or more Insight memories. Driven by
/// <see cref="IntakeOptions.DocsPattern"/>: when the option is unset the extractor is
/// inactive, so the whole-repo intake flow leaves docs trees alone unless the docs verb
/// is invoked explicitly.
/// </summary>
public sealed class DocsFolderExtractor : IIntakeExtractor
{
    public string Name => "markdown.docs-folder";

    public bool AppliesTo(IntakeContext ctx) =>
        !string.IsNullOrEmpty(ctx.Options.DocsPattern) && Directory.Exists(ctx.ProjectPath);

    public async Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct)
    {
        var pattern = ctx.Options.DocsPattern!;
        var searchOption = ctx.Options.DocsRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var importance = ctx.Options.DocsImportance;
        var extraTags = ctx.Options.DocsExtraTags;

        foreach (var file in Directory.GetFiles(ctx.ProjectPath, pattern, searchOption))
        {
            var relativePath = Path.GetRelativePath(ctx.ProjectPath, file);
            var content = await File.ReadAllTextAsync(file, ct);

            var fileNameTags = MarkdownIntake.TagsFromFileName(relativePath);

            foreach (var (sectionContent, headingTags) in MarkdownIntake.SplitByHeadings(content))
            {
                var tags = new List<string>(headingTags);
                tags.AddRange(fileNameTags);
                if (extraTags is { Count: > 0 }) tags.AddRange(extraTags);
                tags = tags.Distinct().ToList();

                await sink.AddMemoryAsync(
                    new IntakeMemory(relativePath, MemoryType.Insight, sectionContent.Trim(), tags, importance),
                    ct);
            }
        }
    }
}
