using System.Text.RegularExpressions;

namespace Eidet.Core.Text;

/// <summary>
/// Shortens text to whole leading sentences: no model call, nothing rewritten. A <c>.</c>, <c>!</c> or <c>?</c> ends a
/// sentence only when whitespace or the end of the text follows it (after any closing <c>*_)"'”’]</c>), so
/// <c>0.5.0</c>, <c>$.store</c> and <c>file.cs</c> never end one, and neither do common abbreviations
/// (<c>e.g.</c>, <c>etc.</c>). A Chinese or Japanese stop ends one without a space after it, a line break ends one,
/// and nothing inside inline code or a code fence ever does.
/// </summary>
/// <remarks>
/// Ported from textfold's <c>fold.ts</c> (PaperFold, Apache License 2.0):
/// https://github.com/chenxiachan/paperfold/blob/0a3f1f5babc2/plugins/textfold/hooks/fold.ts.
/// Line breaks as sentence ends are Eidet's addition: fold.ts joins a block's lines before it folds them, while
/// memory content keeps its headings and list items, which rarely end in a stop.
/// </remarks>
public static partial class TextFold
{
    private const string Closers = "*_)\"'”’]";

    /// <summary>The text's first sentence, trimmed, with a bold or code span the cut left open closed again.</summary>
    public static string FirstSentence(string text) => Balance(text[..SentenceEnd(text, 0)].Trim());

    /// <summary>
    /// The trimmed text if it fits <paramref name="maxChars"/>; otherwise as many whole leading sentences as fit,
    /// followed by " …". Only when the first sentence alone is over budget is the text cut mid-sentence.
    /// </summary>
    public static string Fit(string text, int maxChars)
    {
        text = text.Trim();
        if (text.Length <= maxChars) return text;

        var kept = "";
        for (var end = SentenceEnd(text, 0); end < text.Length; end = SentenceEnd(text, end))
        {
            var head = Balance(text[..end].Trim());
            if (head.Length > maxChars) break;
            kept = head;
        }
        return kept.Length > 0 ? kept + " …" : text[..maxChars] + "…";
    }

    /// <summary>Where the sentence starting at <paramref name="start"/> ends: past its stop and any closers, at a
    /// line break, or at the end of the text.</summary>
    private static int SentenceEnd(string text, int start)
    {
        var i = start;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

        var inCode = false;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '`')
            {
                inCode = !inCode;
                continue;
            }
            if (inCode) continue;
            if (c is '\n' or '\r') return i;
            if (c is '。' or '！' or '？') return i + 1;
            if (c is not ('.' or '!' or '?')) continue;

            var end = i + 1;
            while (end < text.Length && Closers.Contains(text[end])) end++;
            if (end < text.Length && !char.IsWhiteSpace(text[end])) continue;   // "3.5", "a.b", "...": not a sentence's end
            if (c == '.' && Abbreviation().IsMatch(text.AsSpan(0, i + 1))) continue;
            return end;
        }
        return text.Length;
    }

    /// <summary>Closes the bold or code span a cut left open.</summary>
    private static string Balance(string s)
    {
        if (s.AsSpan().Count("**") % 2 == 1) s += "**";
        if (s.AsSpan().Count('`') % 2 == 1) s += "`";
        return s;
    }

    [GeneratedRegex(@"\b(?:e\.g|i\.e|etc|vs|cf|al|approx|Dr|Mr|Ms|Fig|Eq|No)\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Abbreviation();
}
