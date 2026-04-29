using System.Text.Json;
using Eidet.Core.Domain;

namespace Eidet.Core.Intake.Extractors;

/// <summary>
/// Reads <c>package.json</c> at the project root and emits one <c>npm:</c>-prefixed
/// <see cref="MemoryLink"/> per entry under <c>dependencies</c> and
/// <c>devDependencies</c>. Malformed JSON is swallowed silently — intake should never
/// fail a whole project because one manifest is broken.
/// </summary>
public sealed class NpmDependencyExtractor : IIntakeExtractor
{
    private const string FileName = "package.json";

    public string Name => "deps.npm";

    public bool AppliesTo(IntakeContext ctx) =>
        File.Exists(Path.Combine(ctx.ProjectPath, FileName));

    public async Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct)
    {
        var path = Path.Combine(ctx.ProjectPath, FileName);
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            using var doc = JsonDocument.Parse(json);
            ExtractSection(doc.RootElement, "dependencies", sink);
            ExtractSection(doc.RootElement, "devDependencies", sink);
        }
        catch (JsonException)
        {
            sink.RecordSkipped(FileName, "malformed JSON");
        }
        catch (IOException)
        {
            sink.RecordSkipped(FileName, "read failed");
        }
    }

    private static void ExtractSection(JsonElement root, string section, IIntakeSink sink)
    {
        if (!root.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in deps.EnumerateObject())
        {
            sink.AddLink(new MemoryLink
            {
                TargetRepoId = $"npm:{prop.Name}",
                Relation = "depends-on",
            });
        }
    }
}
