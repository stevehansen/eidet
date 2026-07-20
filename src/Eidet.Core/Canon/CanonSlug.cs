using System.Text;

namespace Eidet.Core.Canon;

/// <summary>
/// Slug derivation for Canon draft ids and <c>canon:*</c> tags: lowercase, every run of
/// non-alphanumeric characters collapsed to a single hyphen, leading/trailing hyphens trimmed.
/// Deterministic so the same term always keys the same draft (the damper depends on it). Entity/alias
/// normalization beyond casing (plural folding) is deferred — CanonSpec open question 1.
/// </summary>
public static class CanonSlug
{
    public static string From(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var sb = new StringBuilder(text.Length);
        var pendingHyphen = false;
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingHyphen && sb.Length > 0) sb.Append('-');
                pendingHyphen = false;
                sb.Append(ch);
            }
            else
            {
                pendingHyphen = true;
            }
        }
        return sb.ToString();
    }
}
