using Eidet.Core.Domain;

namespace Eidet.Core.Intake.Extractors;

/// <summary>
/// Captures up to 20 effective lines from .editorconfig as a single Insight memory.
/// Comment lines (<c>#</c>, <c>;</c>) and blank lines are skipped; the result is
/// stored verbatim rather than summarised so format preferences round-trip cleanly.
/// </summary>
public sealed class EditorConfigExtractor : IIntakeExtractor
{
    private const string FileName = ".editorconfig";
    private const int MaxLines = 20;

    public string Name => "markdown.editorconfig";

    public bool AppliesTo(IntakeContext ctx) =>
        File.Exists(Path.Combine(ctx.ProjectPath, FileName));

    public async Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct)
    {
        var path = Path.Combine(ctx.ProjectPath, FileName);
        var content = await File.ReadAllTextAsync(path, ct);
        var summary = ParseEditorConfig(content);
        if (string.IsNullOrWhiteSpace(summary)) return;

        await sink.AddMemoryAsync(
            new IntakeMemory(FileName, MemoryType.Insight, summary, ["editorconfig", "formatting"], Importance: 0.35f),
            ct);
    }

    private static string ParseEditorConfig(string content)
    {
        var lines = content.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#') && !l.StartsWith(';') && l.Contains('='))
            .Take(MaxLines);
        return $"EditorConfig settings:\n{string.Join("\n", lines)}";
    }
}
