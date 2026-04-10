using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Services;

public partial class IntakeService
{
    private readonly IEidetStore _store;

    public IntakeService(IEidetStore store)
    {
        _store = store;
    }

    public async Task<IntakeResult> IngestAsync(string repoId, string projectPath, bool dryRun = false, CancellationToken ct = default)
    {
        var result = new IntakeResult();
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);

        // CLAUDE.md
        var claudeMd = Path.Combine(projectPath, "CLAUDE.md");
        if (File.Exists(claudeMd))
            await IngestMarkdownFile(normalizedRepoId, claudeMd, "CLAUDE.md", MemoryType.Insight, 0.8f, result, dryRun, ct);

        // MEMORY.md (legacy Claude Code memory format)
        var memoryMd = Path.Combine(projectPath, "MEMORY.md");
        if (File.Exists(memoryMd))
            await IngestMarkdownFile(normalizedRepoId, memoryMd, "MEMORY.md", MemoryType.Insight, 0.8f, result, dryRun, ct);

        // README.md
        var readme = Path.Combine(projectPath, "README.md");
        if (File.Exists(readme))
            await IngestMarkdownFile(normalizedRepoId, readme, "README.md", MemoryType.Insight, 0.6f, result, dryRun, ct);

        // .editorconfig
        var editorConfig = Path.Combine(projectPath, ".editorconfig");
        if (File.Exists(editorConfig))
        {
            var content = await File.ReadAllTextAsync(editorConfig, ct);
            var summary = ParseEditorConfig(content);
            if (!string.IsNullOrWhiteSpace(summary))
                await AddIntakeItem(normalizedRepoId, ".editorconfig", MemoryType.Insight, summary, ["editorconfig", "formatting"], 0.7f, result, dryRun, ct);
        }

        // Dependency detection
        result.DetectedLinks = await DetectDependencies(projectPath, ct);
        result.ProducedPackages = DetectProducedPackages(projectPath);

        return result;
    }

    public async Task<IntakeResult> IngestDocsAsync(
        string repoId, string docsPath, bool recursive = true, string pattern = "*.md",
        float importance = 0.6f, List<string>? extraTags = null, bool dryRun = false, CancellationToken ct = default)
    {
        var result = new IntakeResult();
        var normalizedRepoId = RepoIdNormalizer.Normalize(repoId);
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var file in Directory.GetFiles(docsPath, pattern, searchOption))
        {
            var relativePath = Path.GetRelativePath(docsPath, file);
            var tags = ExtractTagsFromFileName(relativePath);
            if (extraTags != null) tags.AddRange(extraTags);

            await IngestMarkdownFile(normalizedRepoId, file, relativePath, MemoryType.Insight, importance, result, dryRun, ct, tags);
        }

        return result;
    }

    private async Task IngestMarkdownFile(
        string repoId, string filePath, string source, MemoryType type, float importance,
        IntakeResult result, bool dryRun, CancellationToken ct, List<string>? extraTags = null)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        var sections = SplitByHeadings(content);

        foreach (var (sectionContent, sectionTags) in sections)
        {
            var tags = new List<string>(sectionTags);
            if (extraTags != null) tags.AddRange(extraTags);
            tags = tags.Distinct().ToList();

            await AddIntakeItem(repoId, source, type, sectionContent.Trim(), tags, importance, result, dryRun, ct);
        }
    }

    private async Task AddIntakeItem(
        string repoId, string source, MemoryType type, string content, List<string> tags,
        float importance, IntakeResult result, bool dryRun, CancellationToken ct)
    {
        var item = new IntakeItem { Source = source, Type = type, Content = content, Tags = tags };

        if (content.Length < 20)
        {
            item.WasSkipped = true;
            item.SkipReason = "too short";
            result.Items.Add(item);
            result.SkippedCount++;
            return;
        }

        // Dedup check via content hash
        var hash = ComputeContentHash(content);
        var id = $"memories/{repoId}/{type.ToString().ToLowerInvariant()}/{hash}";
        var existing = await _store.GetAsync(id, ct);
        if (existing != null)
        {
            item.WasSkipped = true;
            item.SkipReason = "duplicate";
            result.Items.Add(item);
            result.SkippedCount++;
            return;
        }

        if (!dryRun)
        {
            var now = DateTime.UtcNow;
            var entry = new MemoryEntry
            {
                Id = id,
                RepoId = repoId,
                Type = type,
                Content = content,
                Tags = tags,
                Importance = importance,
                Source = "intake",
                Provenance = MemoryProvenance.Intake,
                Confidence = 0.7f,
                CreatedAt = now,
                Validity = new Validity { ValidFrom = now },
                Entities = EntityExtractor.Extract(content),
                OneLiner = EntityExtractor.GenerateHeuristicOneLiner(content),
            };
            await _store.StoreAsync(entry, ct);
        }

        result.Items.Add(item);
        result.NewCount++;
    }

    internal static List<(string Content, List<string> Tags)> SplitByHeadings(string content)
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

            if (sectionContent.Length >= 20)
                sections.Add((sectionContent, tags));
        }

        return sections;
    }

    private static List<string> TagsFromHeading(string heading)
    {
        return heading
            .Split([' ', '-', '_', '/', '\\', '(', ')', '[', ']', '{', '}', '.', ',', ':', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    private static List<string> ExtractTagsFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 2)
            .ToList();
    }

    private static string ParseEditorConfig(string content)
    {
        var lines = content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#') && !l.StartsWith(';') && l.Contains('='))
            .Take(20);
        return $"EditorConfig settings:\n{string.Join("\n", lines)}";
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static async Task<List<MemoryLink>> DetectDependencies(string projectPath, CancellationToken ct)
    {
        var links = new List<MemoryLink>();

        // NuGet (.csproj)
        foreach (var csproj in Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories))
        {
            var content = await File.ReadAllTextAsync(csproj, ct);
            foreach (Match m in PackageReferenceRegex().Matches(content))
            {
                links.Add(new MemoryLink
                {
                    TargetRepoId = $"nuget:{m.Groups[1].Value}",
                    Relation = "depends-on",
                });
            }
        }

        // npm (package.json)
        var packageJson = Path.Combine(projectPath, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                var json = await File.ReadAllTextAsync(packageJson, ct);
                using var doc = JsonDocument.Parse(json);
                ExtractNpmDeps(doc.RootElement, "dependencies", links);
                ExtractNpmDeps(doc.RootElement, "devDependencies", links);
            }
            catch { }
        }

        return links;
    }

    private static void ExtractNpmDeps(JsonElement root, string section, List<MemoryLink> links)
    {
        if (!root.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in deps.EnumerateObject())
        {
            links.Add(new MemoryLink
            {
                TargetRepoId = $"npm:{prop.Name}",
                Relation = "depends-on",
            });
        }
    }

    private static List<string> DetectProducedPackages(string projectPath)
    {
        var packages = new List<string>();
        foreach (var csproj in Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csproj);
            var idMatch = PackageIdRegex().Match(content);
            if (idMatch.Success) packages.Add(idMatch.Groups[1].Value);
        }
        return packages;
    }

    [GeneratedRegex(@"^#{1,3}\s+(.+)", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"<PackageReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PackageReferenceRegex();

    [GeneratedRegex(@"<PackageId>([^<]+)</PackageId>")]
    private static partial Regex PackageIdRegex();
}

public class IntakeResult
{
    public List<IntakeItem> Items { get; set; } = [];
    public int NewCount { get; set; }
    public int SkippedCount { get; set; }
    public List<MemoryLink> DetectedLinks { get; set; } = [];
    public List<string> ProducedPackages { get; set; } = [];
}

public class IntakeItem
{
    public string Source { get; set; } = "";
    public MemoryType Type { get; set; }
    public string Content { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public bool WasSkipped { get; set; }
    public string? SkipReason { get; set; }
}
