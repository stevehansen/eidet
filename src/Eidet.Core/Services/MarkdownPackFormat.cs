using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Eidet.Core.Domain;
using Eidet.Core.Memory;

namespace Eidet.Core.Services;

/// <summary>
/// Serializes/deserializes EidetPack to/from a human-readable markdown format.
///
/// Format:
///   - YAML frontmatter: pack metadata (title, eidet.id, eidet.version, etc.)
///   - H1: pack title + optional intro paragraph
///   - H2: type groups (Observations, Insights, Procedures, Heuristics)
///   - H3: individual memories (heading = one-liner)
///   - HTML comments: per-memory metadata (importance, confidence, tags, entities, etc.)
///   - Content between H3s: memory content
///
/// Designed for publishing on ScribeGate (scribegate.dev) and readable in any markdown viewer.
/// </summary>
public static partial class MarkdownPackFormat
{
    // ─── Serialize ──────────────────────────────────────────────────────

    public static string Serialize(EidetPack pack)
    {
        var sb = new StringBuilder();

        // YAML frontmatter
        sb.AppendLine("---");
        sb.AppendLine($"title: {YamlEscape(pack.Name)}");
        if (!string.IsNullOrWhiteSpace(pack.Description))
            sb.AppendLine($"description: {YamlEscape(pack.Description)}");
        sb.AppendLine("eidet:");
        sb.AppendLine($"  id: {YamlEscape(pack.Id)}");
        sb.AppendLine($"  version: {YamlEscape(pack.Version)}");
        sb.AppendLine($"  author: {YamlEscape(pack.Author)}");
        if (pack.ApplicablePackages.Count > 0)
            sb.AppendLine($"  applicablePackages: [{string.Join(", ", pack.ApplicablePackages.Select(YamlEscape))}]");
        sb.AppendLine($"  createdAt: {pack.CreatedAt:O}");

        // Collect all unique tags across entries for frontmatter
        var allTags = pack.Entries
            .SelectMany(e => e.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (allTags.Count > 0)
            sb.AppendLine($"tags: [{string.Join(", ", allTags.Select(YamlEscape))}]");

        sb.AppendLine("---");
        sb.AppendLine();

        // H1 title + description
        sb.AppendLine($"# {pack.Name}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(pack.Description))
        {
            sb.AppendLine(pack.Description);
            sb.AppendLine();
        }

        // Group entries by type, only emit non-empty groups
        var typeOrder = new[] { MemoryType.Observation, MemoryType.Insight, MemoryType.Procedure, MemoryType.Heuristic };
        foreach (var type in typeOrder)
        {
            var entries = pack.Entries
                .Where(e => e.Type == type)
                .OrderByDescending(e => e.Importance)
                .ToList();

            if (entries.Count == 0) continue;

            sb.AppendLine($"## {TypeToPlural(type)}");
            sb.AppendLine();

            foreach (var entry in entries)
            {
                SerializeEntry(sb, entry);
            }
        }

        return sb.ToString().Replace("\r\n", "\n").TrimEnd() + "\n";
    }

    internal static void SerializeEntry(StringBuilder sb, MemoryEntry entry)
    {
        // H3 heading = one-liner or truncated content
        var heading = entry.OneLiner ?? StringUtils.Truncate(entry.Content, 80);
        sb.AppendLine($"### {heading}");

        // Primary metadata comment
        var meta = new List<string>
        {
            $"importance={entry.Importance.ToString("F2", CultureInfo.InvariantCulture)}",
            $"confidence={entry.Confidence.ToString("F2", CultureInfo.InvariantCulture)}",
        };
        if (entry.Tags.Count > 0)
            meta.Add($"tags={string.Join(",", entry.Tags)}");
        // AgentInferred is omitted as the historical wire default. Unknown is omitted because it must never
        // cross the wire at all (#80): a foreign install has to apply ITS own default rather than inherit
        // our failure to establish provenance, and the importer's clamp then holds it at the Pack floor.
        if (entry.Provenance is not (MemoryProvenance.AgentInferred or MemoryProvenance.Unknown))
            meta.Add($"provenance={entry.Provenance.ToString().ToLowerInvariant()}");
        if (entry.Valence != Valence.Neutral)
            meta.Add($"valence={entry.Valence.ToString().ToLowerInvariant()}");
        if (entry.Stage != FunctionalStage.None)
            meta.Add($"stage={entry.Stage.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(entry.Source))
            meta.Add($"source={entry.Source}");
        if (entry.DerivedFrom.Count > 0)
            meta.Add($"derivedFrom={string.Join(",", entry.DerivedFrom)}");

        sb.AppendLine($"<!-- eidet: {string.Join(" ", meta)} -->");

        // Entities comment (separate for readability)
        if (entry.Entities.Count > 0)
            sb.AppendLine($"<!-- eidet-entities: {string.Join(", ", entry.Entities)} -->");

        // Enrichment comments
        if (!string.IsNullOrWhiteSpace(entry.Summary))
            sb.AppendLine($"<!-- eidet-summary: {EscapeHtmlComment(entry.Summary)} -->");
        if (!string.IsNullOrWhiteSpace(entry.ForesightHint))
            sb.AppendLine($"<!-- eidet-foresight: {EscapeHtmlComment(entry.ForesightHint)} -->");

        sb.AppendLine();

        // Memory content
        sb.AppendLine(entry.Content.TrimEnd());
        sb.AppendLine();
    }

    // ─── Deserialize ────────────────────────────────────────────────────

    public static EidetPack Deserialize(string markdown)
    {
        var (frontmatter, body) = SplitFrontmatter(markdown);
        var fm = ParseFrontmatter(frontmatter);

        var pack = new EidetPack
        {
            Id = GetNested(fm, "eidet", "id") ?? "",
            Name = fm.GetValueOrDefault("title") ?? "",
            Version = GetNested(fm, "eidet", "version") ?? "",
            Author = GetNested(fm, "eidet", "author") ?? "",
            Description = fm.GetValueOrDefault("description"),
            ApplicablePackages = ParseInlineList(GetNested(fm, "eidet", "applicablepackages")),
        };

        // Parse createdAt
        var createdAtStr = GetNested(fm, "eidet", "createdat");
        if (createdAtStr != null && DateTime.TryParse(createdAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
            pack.CreatedAt = createdAt;

        // Parse entries from body
        pack.Entries = ParseEntries(body, pack.Id);

        return pack;
    }

    // ─── Frontmatter Parsing ────────────────────────────────────────────

    internal static (string Frontmatter, string Body) SplitFrontmatter(string markdown)
    {
        var text = markdown.TrimStart();
        if (!text.StartsWith("---"))
            return ("", markdown);

        var endIndex = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return ("", markdown);

        var frontmatter = text[3..endIndex].Trim();
        var body = text[(endIndex + 4)..].TrimStart('\r', '\n');
        return (frontmatter, body);
    }

    /// <summary>
    /// Simple YAML-subset parser. Handles:
    /// - key: value
    /// - key: [item1, item2] (inline arrays)
    /// - Nested objects via indentation (one level: eidet.id, eidet.version, etc.)
    ///
    /// All keys are lowercased for case-insensitive lookup.
    /// Nested keys stored as "parent.child".
    /// </summary>
    internal static Dictionary<string, string> ParseFrontmatter(string frontmatter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(frontmatter))
            return result;

        string? currentParent = null;
        foreach (var rawLine in frontmatter.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            var indent = line.Length - line.TrimStart().Length;

            if (indent >= 2 && currentParent != null)
            {
                // Nested key
                var trimmed = line.TrimStart();
                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx > 0)
                {
                    var key = trimmed[..colonIdx].Trim();
                    var value = trimmed[(colonIdx + 1)..].Trim();
                    result[$"{currentParent}.{key}"] = value;
                }
            }
            else
            {
                // Top-level key
                var colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                {
                    var key = line[..colonIdx].Trim();
                    var value = line[(colonIdx + 1)..].Trim();

                    if (string.IsNullOrEmpty(value))
                    {
                        // This is a parent key (e.g., "eidet:")
                        currentParent = key;
                    }
                    else
                    {
                        currentParent = null;
                        result[key] = value;
                    }
                }
            }
        }

        return result;
    }

    internal static string? GetNested(Dictionary<string, string> fm, string parent, string child)
    {
        return fm.GetValueOrDefault($"{parent}.{child}");
    }

    internal static List<string> ParseInlineList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        // Handle [item1, item2] format
        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            trimmed = trimmed[1..^1];

        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    // ─── Entry Parsing ──────────────────────────────────────────────────

    internal static List<MemoryEntry> ParseEntries(string body, string packId)
    {
        var entries = new List<MemoryEntry>();
        MemoryType? currentType = null;

        var lines = body.Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd('\r');

            // H2 = type group ONLY if it matches a known type plural
            if (line.StartsWith("## "))
            {
                var typeStr = line[3..].Trim();
                var parsedType = PluralToType(typeStr);
                if (parsedType != null)
                {
                    currentType = parsedType;
                    i++;
                    continue;
                }
            }

            // H3 = memory entry ONLY if followed by <!-- eidet: metadata comment
            if (line.StartsWith("### ") && IsMemoryBoundary(lines, i))
            {
                var oneLiner = line[4..].Trim();
                i++;

                // Collect metadata comments and content
                var metaComments = new List<string>();
                var contentLines = new List<string>();
                bool inContent = false;

                while (i < lines.Length)
                {
                    var entryLine = lines[i].TrimEnd('\r');

                    // Stop at next type group (H2 matching known type)
                    if (entryLine.StartsWith("## ") && PluralToType(entryLine[3..].Trim()) != null)
                        break;

                    // Stop at next memory entry (H3 followed by eidet comment)
                    if (entryLine.StartsWith("### ") && IsMemoryBoundary(lines, i))
                        break;

                    if (!inContent && entryLine.StartsWith("<!-- eidet"))
                    {
                        metaComments.Add(entryLine);
                        i++;
                        continue;
                    }

                    // First non-comment, non-blank line starts content
                    if (!inContent && string.IsNullOrWhiteSpace(entryLine))
                    {
                        i++;
                        continue;
                    }

                    inContent = true;
                    contentLines.Add(entryLine);
                    i++;
                }

                // Trim trailing blank lines from content
                while (contentLines.Count > 0 && string.IsNullOrWhiteSpace(contentLines[^1]))
                    contentLines.RemoveAt(contentLines.Count - 1);

                var content = string.Join("\n", contentLines);
                if (string.IsNullOrWhiteSpace(content)) continue;

                var entry = new MemoryEntry
                {
                    Type = currentType ?? MemoryType.Insight,
                    Content = content,
                    OneLiner = oneLiner,
                    Provenance = MemoryProvenance.Pack,
                    Source = "markdown-pack",
                    IsLatest = true,
                    CreatedAt = DateTime.UtcNow,
                    LayerId = string.IsNullOrEmpty(packId) ? null : $"pack:{packId}",
                };

                // Parse metadata from HTML comments
                foreach (var comment in metaComments)
                    ApplyMetadataComment(entry, comment);

                // Imported pack content is untrusted-until-echoed regardless of what the pack DECLARES.
                // A poisoned pack (MemoryGraft, #34 / STRIDE T-7) controls its own bytes and could write
                // `provenance=userStated` to self-assign full trust and dodge the Pack floor. Clamp any
                // declared provenance that would raise the trust floor above Pack's back down to Pack;
                // lower- or equal-trust origins (e.g. Intake) are left as declared.
                if (MemoryTrust.ProvenanceTrust(entry.Provenance) > MemoryTrust.ProvenanceTrust(MemoryProvenance.Pack))
                    entry.Provenance = MemoryProvenance.Pack;

                // Generate deterministic ID
                entry.RepoId = packId;
                entry.Id = MemoryIdGenerator.Generate(packId, entry.Type, entry.Content, entry.CreatedAt);

                entries.Add(entry);
                continue;
            }

            i++;
        }

        return entries;
    }

    /// <summary>
    /// Checks whether an H3 at position i is a memory boundary (vs markdown in content).
    /// An H3 is a memory boundary only if it's followed by an eidet metadata comment
    /// within the next 4 non-blank lines.
    /// </summary>
    internal static bool IsMemoryBoundary(string[] lines, int h3Index)
    {
        for (int j = h3Index + 1; j < lines.Length && j <= h3Index + 4; j++)
        {
            var line = lines[j].TrimEnd('\r').TrimStart();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("<!-- eidet"))
                return true;
            // First non-blank, non-comment line means this isn't a boundary
            return false;
        }
        return false;
    }

    internal static void ApplyMetadataComment(MemoryEntry entry, string comment)
    {
        // <!-- eidet: importance=0.85 confidence=0.75 tags=react,hooks provenance=userStated -->
        var match = EidetMetaRegex().Match(comment);
        if (match.Success)
        {
            var pairs = ParseMetaPairs(match.Groups[1].Value);
            if (pairs.TryGetValue("importance", out var imp) &&
                float.TryParse(imp, CultureInfo.InvariantCulture, out var impVal))
                entry.Importance = impVal;
            if (pairs.TryGetValue("confidence", out var conf) &&
                float.TryParse(conf, CultureInfo.InvariantCulture, out var confVal))
                entry.Confidence = confVal;
            if (pairs.TryGetValue("tags", out var tags))
                entry.Tags = [.. tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            if (pairs.TryGetValue("provenance", out var prov) &&
                Enum.TryParse<MemoryProvenance>(prov, ignoreCase: true, out var provVal))
                entry.Provenance = provVal;
            if (pairs.TryGetValue("valence", out var val) &&
                Enum.TryParse<Valence>(val, ignoreCase: true, out var vv))
                entry.Valence = vv;
            if (pairs.TryGetValue("stage", out var stg) &&
                Enum.TryParse<FunctionalStage>(stg, ignoreCase: true, out var sv))
                entry.Stage = sv;
            if (pairs.TryGetValue("source", out var src))
                entry.Source = src;
            if (pairs.TryGetValue("derivedfrom", out var df))
                entry.DerivedFrom = [.. df.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            return;
        }

        // <!-- eidet-entities: useState, useEffect -->
        var entitiesMatch = EidetEntitiesRegex().Match(comment);
        if (entitiesMatch.Success)
        {
            entry.Entities = [.. entitiesMatch.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            return;
        }

        // <!-- eidet-summary: ... -->
        var summaryMatch = EidetSummaryRegex().Match(comment);
        if (summaryMatch.Success)
        {
            entry.Summary = UnescapeHtmlComment(summaryMatch.Groups[1].Value.Trim());
            return;
        }

        // <!-- eidet-foresight: ... -->
        var foresightMatch = EidetForesightRegex().Match(comment);
        if (foresightMatch.Success)
        {
            entry.ForesightHint = UnescapeHtmlComment(foresightMatch.Groups[1].Value.Trim());
            return;
        }
    }

    internal static Dictionary<string, string> ParseMetaPairs(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Match key=value pairs (value can contain commas for tags)
        foreach (Match m in MetaPairRegex().Matches(text))
        {
            result[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return result;
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    internal static string TypeToPlural(MemoryType type) => type switch
    {
        MemoryType.Observation => "Observations",
        MemoryType.Insight => "Insights",
        MemoryType.Procedure => "Procedures",
        MemoryType.Heuristic => "Heuristics",
        _ => type.ToString() + "s",
    };

    internal static MemoryType? PluralToType(string plural)
    {
        var normalized = plural.Trim().ToLowerInvariant();
        return normalized switch
        {
            "observations" => MemoryType.Observation,
            "insights" => MemoryType.Insight,
            "procedures" => MemoryType.Procedure,
            "heuristics" => MemoryType.Heuristic,
            _ => null,
        };
    }

    internal static string YamlEscape(string value)
    {
        // Quote if contains special YAML characters
        if (value.Contains(':') || value.Contains('#') || value.Contains('"') ||
            value.Contains('\'') || value.Contains('\n') || value.Contains('{') ||
            value.Contains('}') || value.Contains('[') || value.Contains(']'))
        {
            return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }
        return value;
    }

    internal static string EscapeHtmlComment(string text)
    {
        // HTML comments cannot contain "--" sequence
        return text.Replace("--", "‑‑"); // Replace with non-breaking hyphens
    }

    internal static string UnescapeHtmlComment(string text)
    {
        return text.Replace("‑‑", "--");
    }

    // ─── Regex ──────────────────────────────────────────────────────────

    [GeneratedRegex(@"<!--\s*eidet:\s*(.+?)\s*-->")]
    private static partial Regex EidetMetaRegex();

    [GeneratedRegex(@"<!--\s*eidet-entities:\s*(.+?)\s*-->")]
    private static partial Regex EidetEntitiesRegex();

    [GeneratedRegex(@"<!--\s*eidet-summary:\s*(.+?)\s*-->")]
    private static partial Regex EidetSummaryRegex();

    [GeneratedRegex(@"<!--\s*eidet-foresight:\s*(.+?)\s*-->")]
    private static partial Regex EidetForesightRegex();

    [GeneratedRegex(@"(\w+)=(\S+)")]
    private static partial Regex MetaPairRegex();
}
