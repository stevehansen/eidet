using System.Text.RegularExpressions;
using Eidet.Core.Text;

namespace Eidet.Core.Services;

public static partial class EntityExtractor
{
    public static List<string> Extract(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in FilePathRegex().Matches(content))
            AddEntity(entities, m.Value);

        foreach (Match m in DottedIdentifierRegex().Matches(content))
            AddEntity(entities, m.Value);

        foreach (Match m in ApiEndpointRegex().Matches(content))
            AddEntity(entities, m.Value);

        foreach (Match m in CliCommandRegex().Matches(content))
            AddEntity(entities, m.Value.Trim());

        foreach (Match m in BacktickCodeRegex().Matches(content))
        {
            var inner = m.Groups[1].Value;
            if (inner.Length >= 3 && inner.Length <= 120)
                AddEntity(entities, inner);
        }

        foreach (Match m in PascalCaseRegex().Matches(content))
            AddEntity(entities, m.Value);

        foreach (Match m in PackageNameRegex().Matches(content))
            AddEntity(entities, m.Value);

        foreach (Match m in ErrorCodeRegex().Matches(content))
            AddEntity(entities, m.Value);

        foreach (Match m in EnvVarRegex().Matches(content))
            AddEntity(entities, m.Value);

        // Hygiene belongs HERE, at the one point every caller derives entities from, and not only at
        // the repair that cleans up after it. Without it the nightly pipeline cannot converge: corpus
        // repair drops a noisy entity, `HeuristicEnrichmentBackfillStage` then sees an empty set and
        // re-derives the same noise from the same content, and the two stages report work against each
        // other forever while the document never actually changes. Measured on a field corpus: one
        // memory survived four consecutive full passes with a 122-char run-on entity, with both stages
        // reporting `Affected: 1` every time and a before/after diff of the whole repo showing zero
        // documents changed. Filtered BEFORE `Take` so noise cannot consume a slot a real identifier
        // would have had.
        return [.. EntityHygiene.Clean(
            entities.Where(e => e.Length >= 3).OrderByDescending(e => e.Length)).Take(20)];
    }

    public static string? GenerateHeuristicOneLiner(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var text = content.Trim();
        var endIdx = text.IndexOfAny(['.', '\n', '\r']);
        if (endIdx > 0 && endIdx < 120)
            text = text[..endIdx];

        while (text.StartsWith('#'))
            text = text[1..];
        text = text.TrimStart();

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 12)
            text = string.Join(' ', words.Take(10)) + "...";

        return text.Length >= 5 ? text : null;
    }

    private static void AddEntity(HashSet<string> entities, string value)
    {
        var trimmed = value.Trim('.', ',', ';', ':', ')', '(', '[', ']', '{', '}', '"', '\'');
        if (trimmed.Length < 3 || trimmed.Length > 150)
            return;
        if (!IsValidEntity(trimmed))
            return;
        entities.Add(trimmed);
    }

    public static bool IsValidEntity(string entity)
    {
        if (entity.Contains('\n') || entity.Contains('\r'))
            return false;
        if (entity.Contains('#'))
            return false;
        if (entity.StartsWith('—') || entity.StartsWith('-') || entity.StartsWith('*'))
            return false;
        if (entity.Contains("  "))
            return false;
        return true;
    }

    // File paths: C:\foo\bar.cs, ./src/file.ts, /usr/bin/thing
    [GeneratedRegex(@"(?:[A-Za-z]:)?(?:[/\\][\w\-. ]+){2,}(?:\.\w+)?")]
    private static partial Regex FilePathRegex();

    // Dotted identifiers: System.IO.File, config.memory.enabled, App.xaml.cs
    [GeneratedRegex(@"\b[A-Z][a-zA-Z0-9]*(?:\.[A-Za-z][a-zA-Z0-9]*){1,6}\b")]
    private static partial Regex DottedIdentifierRegex();

    // API endpoints: /api/v2/users, /health, /api/memory/context
    [GeneratedRegex(@"(?:GET|POST|PUT|DELETE|PATCH)?\s*/(?:api|v\d+)/[\w/\-{}]+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiEndpointRegex();

    // CLI commands: dotnet build, git status, npm install, docker compose
    [GeneratedRegex(@"\b(?:dotnet|git|npm|yarn|pnpm|docker|kubectl|az|gh|safe|host|claude|pip|cargo|go)\s+[\w\-]+(?:\s+[\w\-.]+)?")]
    private static partial Regex CliCommandRegex();

    // Backtick code spans: `MemoryService.StoreAsync`, `--force`
    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex BacktickCodeRegex();

    // PascalCase identifiers (2+ words): MemoryService, StoreAsync, MainViewModel
    [GeneratedRegex(@"\b[A-Z][a-z]+(?:[A-Z][a-z]+){1,5}\b")]
    private static partial Regex PascalCaseRegex();

    // Package names: Microsoft.Extensions.AI, @anthropic-ai/sdk
    [GeneratedRegex(@"\b(?:[A-Z][a-z]+\.){2,}[A-Z][a-z]+\b|@[\w\-]+/[\w\-]+")]
    private static partial Regex PackageNameRegex();

    // Error codes: HTTP 404, E0001, CS8602, TS2345
    [GeneratedRegex(@"\b(?:HTTP\s+\d{3}|[A-Z]{1,3}\d{4,5})\b")]
    private static partial Regex ErrorCodeRegex();

    // Environment variables: ASPNETCORE_ENVIRONMENT, NODE_ENV (require underscore to avoid prose words)
    [GeneratedRegex(@"\b[A-Z][A-Z0-9]*_[A-Z0-9_]{2,}\b")]
    private static partial Regex EnvVarRegex();
}
