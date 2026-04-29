using System.Text.RegularExpressions;

namespace Eidet.Core.Intake;

/// <summary>
/// Markdown helpers shared by the CLAUDE/README/docs-folder extractors. Pure functions —
/// no I/O, no store access. Section minimum length and tag-mining rules live here so all
/// markdown extractors stay in lock-step.
/// </summary>
public static partial class MarkdownIntake
{
    /// <summary>Minimum section character count below which a chunk is dropped as low-signal.</summary>
    public const int MinSectionLength = 20;

    /// <summary>
    /// Split a markdown body on H1–H3 headings. Each returned section starts at the
    /// heading line and keeps the heading text in its body. Sections shorter than
    /// <see cref="MinSectionLength"/> are filtered. If the body has no headings,
    /// the whole content is returned as a single tag-less section (unless empty).
    /// </summary>
    public static List<(string Content, List<string> Tags)> SplitByHeadings(string content)
    {
        var sections = new List<(string Content, List<string> Tags)>();
        var matches = HeadingRegex().Matches(content);

        if (matches.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(content))
                sections.Add((content, []));
            return sections;
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
            var sectionContent = content[start..end].Trim();
            var heading = matches[i].Groups[1].Value;
            var tags = TagsFromHeading(heading);

            if (sectionContent.Length >= MinSectionLength)
                sections.Add((sectionContent, tags));
        }

        return sections;
    }

    /// <summary>Mine word-tags from a heading (lower-cased, deduped, length ≥ 2).</summary>
    public static List<string> TagsFromHeading(string heading) =>
        heading
            .Split([' ', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}', '.', ',', ':', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();

    /// <summary>Mine tags from a file name (without extension); rules mirror <see cref="TagsFromHeading"/>.</summary>
    public static List<string> TagsFromFileName(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName)
            .Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 2)
            .ToList();

    [GeneratedRegex(@"^#{1,3}\s+(.+)", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();
}
