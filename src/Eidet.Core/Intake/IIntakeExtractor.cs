namespace Eidet.Core.Intake;

/// <summary>
/// Pluggable intake step — one ecosystem or document type per extractor. SDK
/// consumers can ship Python (pyproject.toml), Go (go.mod), Rust (Cargo.toml),
/// or PyPI extractors without forking Eidet by registering a new implementation
/// with <see cref="Services.IntakeService"/>.
/// </summary>
public interface IIntakeExtractor
{
    /// <summary>Stable identifier for diagnostics (e.g. "markdown.claude", "deps.nuget").</summary>
    string Name { get; }

    /// <summary>Fast probe — return true only when this extractor has work to do.</summary>
    bool AppliesTo(IntakeContext ctx);

    /// <summary>Walk the project, parse files, emit memories/links to the sink.</summary>
    Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct);
}
