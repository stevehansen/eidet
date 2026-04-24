namespace Eidet.Core.Text;

/// <summary>
/// Jaccard-style word-overlap similarity with punctuation stripping and
/// case-insensitive token comparison. Used by the dedup sweep and the
/// quality "potential conflicts" check.
/// </summary>
public static class WordSimilarity
{
    private static readonly char[] Separators =
        [' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\''];

    public static float Compute(string a, string b)
    {
        var wordsA = Tokenize(a);
        var wordsB = Tokenize(b);
        if (wordsA.Count == 0 && wordsB.Count == 0) return 1.0f;
        if (wordsA.Count == 0 || wordsB.Count == 0) return 0.0f;

        var intersection = wordsA.Intersect(wordsB, StringComparer.OrdinalIgnoreCase).Count();
        var union = wordsA.Union(wordsB, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0f : (float)intersection / union;
    }

    private static HashSet<string> Tokenize(string text) => new(
        text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1),
        StringComparer.OrdinalIgnoreCase);
}
