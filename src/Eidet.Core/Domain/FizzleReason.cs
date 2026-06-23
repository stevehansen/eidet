namespace Eidet.Core.Domain;

/// <summary>
/// Optional taxonomy on a Fizzle. Content-invalidating reasons (VersionDrift, Incorrect) signal the
/// memory's substance is wrong — not merely mis-recalled — and so penalize harder than a plain fizzle.
/// Plain enum (no converter), matching <see cref="MemoryType"/>: RavenDB persists it as a stable
/// string by default, and System.Text.Json callers serialize it via their JsonStringEnumConverter.
/// </summary>
public enum FizzleReason
{
    WrongContext,
    Incorrect,
    VersionDrift,
    Other,
}

/// <summary>Policy home for the fizzle-penalty tier — one place to ask "does this reason invalidate
/// the memory's content?" so the steeper penalty stays in sync across the recall and feedback paths.</summary>
public static class FizzleReasons
{
    /// <summary>VersionDrift/Incorrect mean the content itself is wrong (framework/version drift, a
    /// factual error) — the first-class lever for the steeper content-invalidating penalty tier.</summary>
    public static bool IsContentInvalidating(FizzleReason? r) =>
        r is FizzleReason.VersionDrift or FizzleReason.Incorrect;
}
