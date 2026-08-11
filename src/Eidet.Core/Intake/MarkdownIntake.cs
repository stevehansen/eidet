using Eidet.Core.Text;
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
    /// True when a section is nothing but headings — no prose, no commands, no body of any kind.
    ///
    /// <see cref="MinSectionLength"/> cannot catch these: it measures length, and "## Development
    /// Patterns" is 23 characters of pure heading. A body-less section is worse than low-signal,
    /// because enrichment will describe it anyway and cannot describe what is not there — a field
    /// corpus accumulated 1,000 of them, and 843 carried a generated one-liner INVENTING a claim the
    /// repo never made ("## Development Patterns" → "Focus on iterative development cycles for
    /// faster, adaptable product improvements"). Since L1 renders the one-liner ahead of the summary,
    /// the fabrication is what reached the wake-up while the summary that honestly said "this is a
    /// heading, not content" stayed hidden. A heading is a label for knowledge, never the knowledge.
    ///
    /// Deliberately narrow: only blank lines, fence delimiters, and the H1–H3 headings
    /// <see cref="SplitByHeadings"/> itself recognizes are discounted, so "## Build" + "dotnet build"
    /// keeps its body and is stored. A deeper heading counts as body here for the same reason it does
    /// there — the splitter never gives it a section of its own. Rejecting merely terse content is not
    /// the job; rejecting empty content is.
    /// </summary>
    public static bool IsHeadingOnly(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return true;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.Length == 0) continue;
            if (HeadingRegex().IsMatch(line)) continue;
            // A fence delimiter opens or closes a block; the language tag on it ("```bash") names the
            // syntax, not the knowledge. The fenced LINES are what carry content, and they still count.
            if (trimmed.StartsWith("```")) continue;

            // One letter or digit outside the structure is a body. The test is deliberately this weak:
            // measuring how MUCH body there is belongs to MinSectionLength, and a floor here would make
            // this predicate quietly reject terse content too — enrichment consults it, so a stricter
            // rule would strand real memories with no summary at all.
            foreach (var c in trimmed)
            {
                if (char.IsLetterOrDigit(c)) return false;
            }
        }

        return true;
    }

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

    /// <summary>
    /// Mine word-tags from a heading. A heading is prose, so the raw split yields function words and
    /// bare numbers ("How to Make Changes" → how/to/make/changes); <see cref="TagHygiene"/> drops
    /// those and caps the result, which is what keeps mined tags narrow enough to filter on.
    /// </summary>
    public static List<string> TagsFromHeading(string heading) =>
        TagHygiene.Clean(heading
            .Split([' ', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}', '.', ',', ':', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Mine tags from a file name (without extension); rules mirror <see cref="TagsFromHeading"/>.</summary>
    public static List<string> TagsFromFileName(string fileName) =>
        TagHygiene.Clean(Path.GetFileNameWithoutExtension(fileName)
            .Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"^#{1,3}\s+(.+)", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();
}
