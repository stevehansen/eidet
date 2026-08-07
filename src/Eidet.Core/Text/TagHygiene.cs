using System.Collections.Frozen;

namespace Eidet.Core.Text;

/// <summary>
/// The single home for "is this tag worth keeping, and how many may a memory carry".
///
/// Tags exist to narrow a recall, so a tag that appears on a large fraction of the corpus costs
/// storage and dilutes every tag-filtered query without ever narrowing one. Two upstream habits
/// produce exactly that: mining word-tags out of prose (a heading split on spaces yields "to",
/// "and", "2026", "04") and unioning tag sets during consolidation (each generation inherits every
/// ancestor's tags, so a re-consolidated memory grows tags without bound — observed at 199 on one
/// entry, averaging 18 across a 15.6k-memory corpus).
///
/// Both are fixed here rather than at the call sites so the mining rule and the growth bound stay
/// in lock-step: <see cref="Clean"/> is idempotent, so it is safe to apply at mining time, at
/// consolidation time, and again at the write gate.
/// </summary>
public static class TagHygiene
{
    /// <summary>
    /// Upper bound on tags per memory. Ranked by <see cref="Clean"/>'s ordering rule, so the cap
    /// drops the least specific tags first rather than truncating arbitrarily.
    /// </summary>
    public const int MaxTags = 12;

    /// <summary>
    /// English function words plus markdown/heading filler. These reach the tag miner only because
    /// headings are prose; none of them can ever narrow a recall.
    /// </summary>
    private static readonly FrozenSet<string> Stopwords = new[]
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "can", "did", "do", "does", "for",
        "from", "had", "has", "have", "how", "if", "in", "into", "is", "it", "its", "may", "must",
        "no", "not", "of", "on", "or", "our", "out", "over", "should", "so", "some", "such", "than",
        "that", "the", "their", "them", "then", "there", "these", "they", "this", "those", "to",
        "up", "use", "using", "via", "was", "we", "were", "what", "when", "where", "which", "while",
        "who", "why", "will", "with", "would", "you", "your",
        // Contentless filler only. Words that merely FEEL generic ("notes", "todo", "overview") are
        // deliberately kept: "todo" is a load-bearing tag here, and a tag that is weak in one repo can
        // be the discriminating one in another. Over-filtering silently loses recall, which is harder
        // to notice than a slightly noisy tag.
        "etc", "misc",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a tag carries no retrieval signal: a function word, a bare number (dates and issue
    /// numbers split out of headings — "2026", "04", "35"), or a one-character fragment.
    /// </summary>
    public static bool IsNoise(string tag)
    {
        var t = tag.AsSpan().Trim();
        if (t.Length < 2) return true;
        // A bare number never identifies a subject. A token that merely CONTAINS digits (h2, utf8,
        // sha256, rfc-22) usually does, so only all-digit tokens are dropped.
        var allDigits = true;
        foreach (var c in t)
        {
            if (!char.IsAsciiDigit(c)) { allDigits = false; break; }
        }
        return allDigits || Stopwords.Contains(t.ToString());
    }

    /// <summary>
    /// Normalizes a tag set: trims, lower-cases, drops noise, de-duplicates, and caps the count.
    ///
    /// Ordering is specificity-first — multi-word tags ("cache-coherence") before single words, and
    /// longer before shorter — so when the cap bites it sheds the vaguest tags rather than whatever
    /// happened to be last. Input order breaks ties, which keeps a caller's deliberate tags ahead of
    /// mined ones when the caller lists them first.
    /// </summary>
    public static List<string> Clean(IEnumerable<string>? tags, int max = MaxTags)
    {
        if (tags is null) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        var ordinal = 0;
        var ranked = new List<(string Tag, int Words, int Len, int Ord)>();

        foreach (var raw in tags)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var t = raw.Trim().ToLowerInvariant();
            if (IsNoise(t) || !seen.Add(t)) continue;
            ranked.Add((t, t.Count(c => c is '-' or '_' or ' '), t.Length, ordinal++));
        }

        foreach (var r in ranked
                     .OrderByDescending(r => r.Words)
                     .ThenByDescending(r => r.Len)
                     .ThenBy(r => r.Ord)
                     .Take(Math.Max(0, max)))
            kept.Add(r.Tag);

        return kept;
    }
}
