namespace Eidet.Core.MemoryTool;

public sealed record MemoryToolOptions
{
    /// <summary>
    /// What to do when the always-on secret scan hits on a write. <see cref="SecretPolicy.Reject"/>
    /// (default) returns an <c>is_error</c> result and stores nothing — round-trip honesty: the model
    /// is never told a write succeeded when the stored bytes differ from what it sent.
    /// <see cref="SecretPolicy.Redact"/> is opt-in write-through: matched spans are replaced with a
    /// stable marker, at the cost that a later verbatim edit on the redacted span will miss.
    /// </summary>
    public SecretPolicy Secrets { get; init; } = SecretPolicy.Reject;

    /// <summary>DoS cap on a single memory file, per Anthropic backend guidance.</summary>
    public long MaxFileBytes { get; init; } = 256 * 1024;
}

public enum SecretPolicy { Reject, Redact }
