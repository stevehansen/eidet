namespace Eidet.Core.Intake;

/// <summary>
/// Per-call context handed to every <see cref="IIntakeExtractor"/>. Carries the
/// already-normalised repo id, the project root, dry-run flag, and any path-scoped
/// options (pattern, importance, extra tags) used by the docs-folder extractor.
/// </summary>
public sealed class IntakeContext
{
    public required string RepoId { get; init; }
    public required string ProjectPath { get; init; }
    public IntakeOptions Options { get; init; } = new();
    public bool DryRun { get; init; }
}

/// <summary>
/// Optional knobs picked up by individual extractors. Whole-repo intake leaves these
/// at their defaults; the docs-folder verb sets <see cref="DocsPattern"/> and friends.
/// </summary>
public sealed class IntakeOptions
{
    /// <summary>Glob pattern for the docs-folder extractor (e.g. <c>*.md</c>).</summary>
    public string? DocsPattern { get; init; }

    /// <summary>Recurse into sub-directories when walking the docs folder.</summary>
    public bool DocsRecursive { get; init; } = true;

    /// <summary>Importance assigned to memories produced by the docs-folder extractor.</summary>
    public float DocsImportance { get; init; } = 0.6f;

    /// <summary>Extra tags appended to every memory produced by the docs-folder extractor.</summary>
    public IReadOnlyList<string>? DocsExtraTags { get; init; }
}
