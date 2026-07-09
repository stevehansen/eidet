namespace Eidet.Core.Domain;

/// <summary>
/// The stance a memory's content takes toward its subject — orthogonal to MemoryType.
/// Neutral = 0 so every pre-existing document backfills to "no stance" with no migration.
/// </summary>
public enum Valence { Neutral = 0, Affirming, Refuting, Cautionary }
