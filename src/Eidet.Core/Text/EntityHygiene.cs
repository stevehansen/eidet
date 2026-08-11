namespace Eidet.Core.Text;

/// <summary>
/// The single home for "is this string an entity, or is it the model talking".
///
/// Entities are meant to be identifiers — project, package, file, class, function, endpoint, config
/// key, error code — and they earn their keep by being exact: the recall index analyzes them with
/// KeywordAnalyzer and cue-anchor expansion looks a query's terms up against them directly. A prose
/// fragment in that field cannot be matched by any cue, so it is pure dilution.
///
/// Two upstream habits put prose there anyway. An entity-extraction prompt answered by a reasoning
/// model can return its own chain of thought instead of the answer ("The user wants me to act as an
/// information extractor", "Scanning the text", a numbered restatement of the entity types it was
/// asked about, and once a bare <c>&lt;channel|&gt;</c> control token) — 443 such strings across 223
/// memories on a field corpus. And summarizing markdown yields structural leftovers: 1,574
/// numbered-list fragments, 338 strings carrying code fences, 241 bare heading markers.
///
/// The rules key on SHAPE, never on wording, because the wording of a model's aside is unbounded
/// while the shape of an identifier is not: identifiers are short, unpunctuated, and never sentences.
/// That asymmetry is what keeps <c>Vidyano.RavenDB</c>, <c>/api/eidet/context</c>, <c>CLAUDE.md</c>
/// and <c>C:\Program Files\Microsoft Visual Studio\MSBuild</c> while dropping the reasoning around
/// them. <see cref="Clean"/> is idempotent, so it is safe at extraction time and again as repair.
/// </summary>
public static class EntityHygiene
{
    /// <summary>
    /// Word ceiling for an entity. Identifiers do not reach it — the longest real ones are paths and
    /// namespaces of four or five space-separated parts — while a model's aside starts around ten.
    /// Set generously on purpose: a noisy entity costs a little dilution, a dropped one costs recall.
    /// </summary>
    private const int MaxWords = 6;

    /// <summary>Upper bound on an entity's length; beyond this it is a run-on, not a name.</summary>
    private const int MaxLength = 120;

    /// <summary>
    /// True when a string is structure or commentary rather than a name. Ordered cheapest-first;
    /// every rule is shape-based, so none of them can be defeated by rephrasing.
    /// </summary>
    public static bool IsNoise(string entity)
    {
        var t = Normalize(entity);
        if (t.Length < 2 || t.Length > MaxLength) return true;

        // Markdown structure: a heading marker or a fence delimiter names a document part, not a thing.
        if (t[0] == '#' || t.StartsWith("```", StringComparison.Ordinal)) return true;

        // "1. Project names" — an enumeration of what was ASKED for, echoed back as if it were found.
        if (char.IsAsciiDigit(t[0]) && NumberedListPrefixLength(t) > 0) return true;

        // Harmony/ChatML control tokens. A leaked one means the whole string is transport framing.
        if (t.Contains("<|", StringComparison.Ordinal) || t.Contains("<channel", StringComparison.OrdinalIgnoreCase))
            return true;

        var words = 1;
        foreach (var c in t)
        {
            if (c is ' ' or '\t') words++;
        }
        return words > MaxWords;
    }

    /// <summary>
    /// Normalizes one entity: trims whitespace and the trailing sentence punctuation a prompt answer
    /// tends to carry. Trailing only — an interior dot is load-bearing in <c>CLAUDE.md</c> and
    /// <c>Vidyano.Core</c>, and a trailing colon is usually a config key written as it appears in YAML
    /// rather than a reason to discard the key.
    /// </summary>
    public static string Normalize(string entity) => entity.Trim().TrimEnd(':', ',', ';', '.', ' ');

    /// <summary>
    /// Normalizes an entity set: trims, drops noise, and de-duplicates case-insensitively while
    /// preserving the caller's order and the casing of the first occurrence. Order is left alone
    /// because entities carry no specificity ranking the way tags do — nothing here truncates, so
    /// nothing depends on what comes first.
    /// </summary>
    public static List<string> Clean(IEnumerable<string>? entities)
    {
        if (entities is null) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();

        foreach (var raw in entities)
        {
            if (string.IsNullOrWhiteSpace(raw) || IsNoise(raw)) continue;
            var normalized = Normalize(raw);
            if (seen.Add(normalized)) kept.Add(normalized);
        }

        return kept;
    }

    /// <summary>
    /// Length of a leading "12. " / "3) " list marker, or 0 when there is none. A digit run alone is
    /// not enough — <c>404</c> and <c>net10.0</c> are entities — it takes the marker punctuation
    /// followed by a space to make it an enumeration.
    /// </summary>
    private static int NumberedListPrefixLength(string t)
    {
        var i = 0;
        while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
        if (i == 0 || i + 1 >= t.Length) return 0;
        return t[i] is '.' or ')' && t[i + 1] == ' ' ? i + 2 : 0;
    }
}
