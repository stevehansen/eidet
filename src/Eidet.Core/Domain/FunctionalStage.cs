namespace Eidet.Core.Domain;

/// <summary>
/// The functional subtask category a memory applies to — orthogonal to <see cref="MemoryType"/>,
/// mirroring <see cref="Valence"/>. Enables the SASA-style "hard-filter by category before semantic
/// match" precision boost. <see cref="None"/> = 0 so every pre-existing document backfills for free,
/// AND carries a first-class semantic: "stage-agnostic / applies broadly" — which is what makes the
/// recall hard-filter safe (a <c>None</c> memory matches any stage query; see the filter's wildcard rule).
/// </summary>
public enum FunctionalStage { None = 0, Analyze, Locate, Edit, Test, Debug, Deploy }
