namespace Eidet.Core.Intake.Git;

/// <summary>
/// Caller surface for git-history intake. The 95% caller never constructs one —
/// <c>IntakeService.IngestGitAsync</c> defaults are safe.
/// </summary>
/// <param name="Since">Exclusive lower-bound commit SHA; null ⇒ the per-repo last-run
/// watermark, or the last <paramref name="MaxCommits"/> when no watermark exists.</param>
/// <param name="MaxCommits">Upper bound on commits examined per run.</param>
/// <param name="IncludeNonConventional">Widens the gate for repos without
/// Conventional-Commits discipline.</param>
public sealed record GitIntakeOptions(
    string? Since = null,
    int MaxCommits = 500,
    bool IncludeNonConventional = false);
