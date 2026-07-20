using System.Runtime.CompilerServices;

namespace Eidet.Core.Canon.Sources;

/// <summary>
/// Term drafts seeded from a repo's <c>UBIQUITOUS_LANGUAGE.md</c>: it parses the markdown glossary tables
/// under each <c>##</c> section heading (rows shaped <c>| **Term** | Definition | Aliases to avoid |</c>)
/// and proposes one Term draft per row. Header/separator rows are skipped because only bolded-term cells
/// yield a term; the narrative "Example dialogue" and "Flagged ambiguities" sections are skipped entirely.
/// UL terms are authored, not memory-derived, so the drafts carry no members — the definition IS the prose.
/// </summary>
public sealed class UbiquitousLanguageDraftSource : ICanonDraftSource
{
    private const string FileName = "UBIQUITOUS_LANGUAGE.md";
    private static readonly string[] SkipSections = ["example dialogue", "flagged ambiguities"];

    public string Name => "ubiquitous-language";

    public bool AppliesTo(CanonProposalContext ctx) =>
        !string.IsNullOrWhiteSpace(ctx.ProjectPath) && File.Exists(Path.Combine(ctx.ProjectPath, FileName));

    public async IAsyncEnumerable<CanonDraftCandidate> ProposeAsync(
        CanonProposalContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = Path.Combine(ctx.ProjectPath, FileName);
        if (!File.Exists(path)) yield break;

        var lines = await File.ReadAllLinesAsync(path, ct);
        string? section = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith("## "))
            {
                section = line[3..].Trim();
                continue;
            }
            if (section is null) continue;
            if (SkipSections.Any(s => section.StartsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
            if (!line.StartsWith('|')) continue;

            var cells = SplitRow(line);
            if (cells.Count < 2) continue;

            var term = ExtractBoldTerm(cells[0]);   // null for header/separator rows — skips them
            if (term is null) continue;

            var definition = cells[1].Trim();
            if (definition.Length == 0) continue;

            var slug = CanonSlug.From(term);
            if (string.IsNullOrEmpty(slug)) continue;

            var content = $"{term}: {definition} (from the \"{section}\" section of {FileName})";
            var fingerprint = CanonFingerprint.Of(CanonKind.Term, term, content, []);

            yield return new CanonDraftCandidate(CanonKind.Term, slug, term, content, [], fingerprint);
        }
    }

    // Split a markdown table row on unescaped pipes, dropping the empty cells the outer pipes create.
    private static List<string> SplitRow(string line) =>
        line.Trim('|')
            .Split('|')
            .Select(c => c.Trim())
            .ToList();

    // A term cell is "**Term**"; return the inner text, or null when the cell is not a bolded term
    // (header cell "Term", separator cell "---", or an empty first cell).
    private static string? ExtractBoldTerm(string cell)
    {
        var t = cell.Trim();
        if (t.Length < 5 || !t.StartsWith("**") || !t.EndsWith("**")) return null;
        var inner = t[2..^2].Trim();
        return inner.Length == 0 ? null : inner;
    }
}
