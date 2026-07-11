namespace Eidet.Core.MemoryTool;

/// <summary>
/// A validated, canonical memory-tool path — the single choke point for path safety.
/// Every path is constrained under the <c>/memories</c> root; traversal segments
/// (<c>.</c>/<c>..</c>), backslashes, control characters, and url-encoded escapes are
/// rejected at construction, so downstream code never sees an unsafe path.
/// The canonical form has no trailing slash and no duplicate separators; the
/// default value is the root.
/// </summary>
public readonly struct MemoryPath : IEquatable<MemoryPath>
{
    public const string Root = "/memories";

    private readonly string? _value;

    private MemoryPath(string canonical) => _value = canonical;

    /// <summary>Canonical path string, e.g. <c>/memories</c> or <c>/memories/plans/auth.md</c>.</summary>
    public string Value => _value ?? Root;

    public bool IsRoot => Value == Root;

    /// <summary>Path relative to the root (blob-key form): <c>""</c> for the root, else e.g. <c>plans/auth.md</c>.</summary>
    public string Relative => IsRoot ? "" : Value[(Root.Length + 1)..];

    /// <summary>True when this path sits at or below <paramref name="dir"/>.</summary>
    public bool IsUnder(MemoryPath dir) =>
        Value == dir.Value || Value.StartsWith(dir.Value + "/", StringComparison.Ordinal);

    /// <summary>Parse and validate, throwing on invalid input — for programmatic construction. Transport boundaries use <see cref="TryParse"/>.</summary>
    public static MemoryPath Of(string raw) =>
        TryParse(raw, out var path, out var error) ? path : throw new ArgumentException(error, nameof(raw));

    /// <summary>
    /// Parse and validate without throwing. On failure <paramref name="error"/> carries a
    /// model-readable reason suitable for an <c>is_error</c> tool result.
    /// </summary>
    public static bool TryParse(string? raw, out MemoryPath path, out string error)
    {
        path = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Invalid path: a path is required and must start with /memories";
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Contains('\\'))
        {
            error = $"Invalid path `{raw}`: backslashes are not allowed. All paths must start with {Root} and use `/` separators.";
            return false;
        }
        if (trimmed.Any(char.IsControl))
        {
            error = $"Invalid path `{raw}`: control characters are not allowed.";
            return false;
        }
        if (trimmed != Root && !trimmed.StartsWith(Root + "/", StringComparison.Ordinal))
        {
            error = $"Invalid path `{raw}`: all paths must start with {Root}";
            return false;
        }

        // Canonicalize: split, drop empty segments (duplicate/trailing slashes), validate each segment.
        var segments = new List<string>();
        foreach (var segment in trimmed[Root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsSafeSegment(segment))
            {
                error = $"Invalid path `{raw}`: path traversal is not allowed. All paths must stay under {Root}.";
                return false;
            }
            segments.Add(segment);
        }

        path = new MemoryPath(segments.Count == 0 ? Root : Root + "/" + string.Join('/', segments));
        error = "";
        return true;
    }

    /// <summary>
    /// A segment is unsafe if it is (or url-decodes to) a traversal token or smuggles a
    /// separator — e.g. <c>..</c>, <c>%2e%2e</c>, <c>%2f</c>, <c>%5c</c>.
    /// </summary>
    private static bool IsSafeSegment(string segment)
    {
        if (segment is "." or "..") return false;
        if (!segment.Contains('%')) return true;

        string decoded;
        try { decoded = Uri.UnescapeDataString(segment); }
        catch (UriFormatException) { return false; }
        return decoded == segment ||
            (decoded is not ("." or "..") && !decoded.Contains('/') && !decoded.Contains('\\') &&
             IsSafeSegment(decoded));
    }

    public override string ToString() => Value;
    public bool Equals(MemoryPath other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is MemoryPath other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public static bool operator ==(MemoryPath left, MemoryPath right) => left.Equals(right);
    public static bool operator !=(MemoryPath left, MemoryPath right) => !left.Equals(right);
}
