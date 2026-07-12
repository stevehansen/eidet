namespace Eidet.Core.Domain;

/// <summary>
/// A recorded contradiction attempt — content that was quarantined for contradicting a high-trust
/// memory. Lives in its own append-only <c>PoisonPatterns</c> collection (the <c>looseends/*</c>
/// precedent) so it never perturbs the <c>memories/*</c> recall cache or any maintenance sweep.
/// The deterministic id (<c>poisonpatterns/{repoId}/{fingerprint}</c>) lets a repeat attempt with
/// the same content fast-path to Rejected, and answers "what keeps coming back, and against what?"
/// with evidence across restarts. No decay.
/// </summary>
public sealed class PoisonPattern
{
    public string Id { get; set; } = "";           // poisonpatterns/{repoId}/{fingerprint}
    public string RepoId { get; set; } = "";
    public string Fingerprint { get; set; } = "";   // SHA256 prefix of the normalized content
    public string ContradictedId { get; set; } = "";
    public Valence Stance { get; set; }
    public Valence ContradictedStance { get; set; }
    public double ContradictedTrust { get; set; }
    public string SampleContent { get; set; } = ""; // first-seen content, for the evidence trail
    public int Attempts { get; set; } = 1;
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}
